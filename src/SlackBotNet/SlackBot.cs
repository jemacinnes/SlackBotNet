using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using SlackBotNet.Messages;
using SlackBotNet.State;
using SlackBotNet.Drivers;
using SlackBotNet.Infrastructure;
using SlackBotNet.Messages.WebApi;

namespace SlackBotNet
{
    public class SlackBot : IDisposable
    {
        private readonly IMessageBus messageBus;
        private readonly SlackBotState state;
        private readonly ISlackBotConfig config;

        private ConcurrentQueue<WhenHandler> whenHandlers;

        private IDriver driver;

        private IDisposable sendTimer = null;

        private SlackBot(
            SlackBotState state,
            IDriver driver,
            IMessageBus bus,
            ISlackBotConfig config)
        {
            this.state = state;
            this.config = config;
            this.driver = driver;

            this.messageBus = bus;

            this.whenHandlers = new ConcurrentQueue<WhenHandler>();
        }

        public IReadOnlyState State => this.state;

        public static Task<SlackBot> InitializeAsync(string slackToken, Action<ISlackBotConfig> config = null)
            => InitializeAsync(slackToken, new SlackRtmDriver(slackToken), new RxMessageBus(), config);

        internal static async Task<SlackBot> InitializeAsync(string slackToken, IDriver driver, IMessageBus bus, Action<ISlackBotConfig> config = null)
        {
            var defaultConfig = new DefaultSlackBotConfig();
            config?.Invoke(defaultConfig);

            var state = await driver.ConnectAsync(bus);

            var bot = new SlackBot(state, driver, bus, defaultConfig);
            bot.StartSendListener();

            bot.On<IHubJoined>(msg =>
            {
                bot.state.AddHub(msg.Channel.Id, msg.Channel.Name, msg.HubType);
                bot.config.TraceHandler($"Joined hub {msg.Channel.Name} (Id: {msg.Channel.Id})");
            });

            bot.On<IHubLeft>(msg =>
            {
                bot.config.TraceHandler($"Left hub {bot.state.GetHubById(msg.Channel).Name}");
                bot.state.RemoveHub(msg.Channel);
            });

            // Handle .When setups
            bot.On<Message>(async msg =>
            {
                // Ignore messages with reply_to that is set.
                // They appear to be sent after the initial connection that the bot establishes.
                if (!string.IsNullOrEmpty(msg.ReplyTo))
                    return;

                (decimal score, Match[] matches, WhenHandler handler) bestMatch = (-1m, null, null);

                foreach (var handler in bot.whenHandlers)
                {
                    var matches = new Match[0];
                    try
                    {
                        matches = await handler.MatchGenerator.Invoke(msg);
                    }
                    catch (Exception exception)
                    {
                        handler.OnException(msg, exception);
                    }

                    if (matches == null)
                        continue;

                    decimal score = matches.Sum(m => m.Score);

                    if (score < 0)
                        continue;

                    if (bot.config.WhenHandlerMatchMode == WhenHandlerMatchMode.AllMatches || bot.config.WhenHandlerMatchMode == WhenHandlerMatchMode.FirstMatch)
                    {
                        var (success, ex) = await handler.MessageHandler(msg, matches);

                        if (ex != null)
                            handler.OnException(msg, ex);

                        if (success && bot.config.WhenHandlerMatchMode == WhenHandlerMatchMode.FirstMatch)
                            break;
                    }

                    if (score > bestMatch.score)
                        bestMatch = (score, matches, handler);
                }

                if (bot.config.WhenHandlerMatchMode == WhenHandlerMatchMode.BestMatch && bestMatch.handler != null)
                    await bestMatch.handler.MessageHandler(msg, bestMatch.matches);
            });

            return bot;
        }

        #region Send

        private readonly ConcurrentQueue<PostMessage> messageQueue = new ConcurrentQueue<PostMessage>();

        /// <summary>
        /// Limits outgoing messages to 1/second
        /// </summary>
        private void StartSendListener()
        {
            this.sendTimer = Observable
                .Timer(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1))
                .Subscribe(async _ =>
                {
                    if (this.messageQueue.TryDequeue(out PostMessage message))
                    {
                        await this.driver.SendMessageAsync(message);
                    }
               });
        }

        public Task SendAsync(Hub hub, string message, params Attachment[] attachments)
            => this.SendAsync(hub.Id, message, attachments);

        public Task SendAsync(string channel, string message, params Attachment[] attachments)
        {
            this.messageQueue.Enqueue(new PostMessage(channel, message, attachments));
            return Task.CompletedTask;
        }

        public Task ReplyAsync(Hub hub, string message, Message replyTo, params Attachment[] attachments)
            => this.ReplyAsync(hub.Id, message, replyTo, attachments);

        public Task ReplyAsync(string channel, string message, Message replyTo, params Attachment[] attachments)
        {
            var ts = !string.IsNullOrEmpty(replyTo.RawThreadTimestamp)
                ? replyTo.RawThreadTimestamp
                : replyTo.ChannelTimestamp;

            this.QueueForSending(new PostMessage(channel, message, attachments) { ThreadTimestamp = ts });
            return Task.CompletedTask;
        }

        private void QueueForSending(PostMessage message) => this.messageQueue.Enqueue(message);

        #endregion

        #region Receive

        public IDisposable On<TMessage>(Action<TMessage> handler)
            where TMessage : IRtmMessage
        {
            return this.messageBus.Observe<TMessage>().Subscribe(handler);
        }

        public IWhenHandler When(MessageMatcher match, Func<IConversation, Task> handler)
            => this.When(match, HubType.All, Modes.None, handler);

        public IWhenHandler When(MessageMatcher match, HubType hubs, Func<IConversation, Task> handler)
            => this.When(match, hubs, Modes.None, handler);

        public IWhenHandler When(MessageMatcher match, Modes modes, Func<IConversation, Task> handler)
            => this.When(match, HubType.All, modes, handler);

        public IWhenHandler When(MessageMatcher match, HubType hubs, Modes modes, Func<IConversation, Task> handler)
        {
            bool MessageAddressesBot(Message msg) => 
                (modes & Modes.ObserveAllMessages) == Modes.ObserveAllMessages 
                || msg.Text.Contains(this.state.BotUserId, StringComparison.OrdinalIgnoreCase) 
                || msg.Text.Contains(this.state.BotUsername, StringComparison.OrdinalIgnoreCase);

            var whenHandler = new WhenHandler(this,
                msg =>
                {
                    if (msg.User != null && msg.User.Equals(this.state.BotUserId))
                        return MessageMatcher.NoMatch;

                    var messageHubType = this.state.GetHubById(msg.Channel).HubType;
                    if ((hubs & messageHubType) != messageHubType)
                        return MessageMatcher.NoMatch;

                    if (messageHubType != HubType.DirectMessage)
                    {
                        if (!MessageAddressesBot(msg))
                            return MessageMatcher.NoMatch;
                    }

                    return match.GetMatches(msg);
                },
                async (msg, matches) =>
                {
                    var modesCopy = modes;

                    // Conversation being initiated from another thread? force threaded mode
                    if (msg.RawThreadTimestamp != null)
                        modesCopy |= Modes.StartThread;

                    using (var conversation = new Conversation(this, modesCopy, msg, matches))
                    {
                        try
                        {
                            await handler(conversation);
                            return (true, null);
                        }
                        catch (Exception ex)
                        {
                            return (false, ex);
                        }
                    }
                });

            this.whenHandlers.Enqueue(whenHandler);
            return whenHandler;
        }

        class WhenHandler : IWhenHandler
        {
            internal event Action<Message, Exception> OnExceptionEvt = delegate {};

            private readonly SlackBot bot;

            public WhenHandler(
                SlackBot bot, 
                Func<Message, Task<Match[]>> matchGenerator,
                Func<Message, Match[], Task<(bool success, Exception ex)>> messageHandler)
            {
                this.bot = bot;
                this.MatchGenerator = matchGenerator;
                this.MessageHandler = messageHandler;
            }

            public Func<Message, Task<Match[]>> MatchGenerator { get; }
            public Func<Message, Match[], Task<(bool success, Exception ex)>> MessageHandler { get; }

            internal void OnException(Message message, Exception ex)
                => this.OnExceptionEvt(message, ex);

            public IWhenHandler OnException(Action<Message, Exception> handler)
            {
                this.OnExceptionEvt += handler;
                return this;
            }

            public void Dispose()
            {
                this.bot.whenHandlers = new ConcurrentQueue<WhenHandler>(
                    this.bot.whenHandlers.Where(m => m != this)
                );
            }
        }

        public interface IWhenHandler : IDisposable
        {
            IWhenHandler OnException(Action<Message, Exception> handler);
        }

        /// <summary>
        /// Returns the first instance of a message of type <typeparamref name="TMessage"/>
        /// that matches the predicate <paramref name="where"/>
        /// </summary>
        /// <typeparam name="TMessage"></typeparam>
        /// <param name="where"></param>
        /// <returns></returns>
        public async Task<TMessage> Linger<TMessage>(Func<TMessage, bool> where)
            where TMessage : IRtmMessage
        {
            return await this.messageBus
                .Observe<TMessage>()
                .Where(where)
                .FirstAsync();
        }

        #endregion

        public void Dispose() => this.sendTimer?.Dispose();
    }
}