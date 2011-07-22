﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Web;
using SignalR.Infrastructure;
using SignalR.Transports;
using SignalR.Web;

namespace SignalR {
    public abstract class PersistentConnection : HttpTaskAsyncHandler, IGroupManager {
        internal const string SignalrCommand = "__SIGNALRCOMMAND__";

        private readonly Signaler _signaler;
        private readonly IMessageStore _store;
        private readonly IJsonStringifier _jsonStringifier;

        protected ITransport _transport;

        protected PersistentConnection()
            : this(Signaler.Instance,
                   DependencyResolver.Resolve<IMessageStore>(),
                   DependencyResolver.Resolve<IJsonStringifier>()) {
        }

        protected PersistentConnection(Signaler signaler,
                                       IMessageStore store,
                                       IJsonStringifier jsonStringifier) {
            _signaler = signaler;
            _store = store;
            _jsonStringifier = jsonStringifier;
        }

        public override bool IsReusable {
            get {
                return false;
            }
        }

        public IConnection Connection {
            get;
            private set;
        }

        private string DefaultSignal {
            get {
                return GetType().FullName;
            }
        }

        public override Task ProcessRequestAsync(HttpContext context) {
            Task task = null;

            if (IsNegotiationRequest(context.Request)) {
                context.Response.ContentType = Json.MimeType;
                context.Response.Write(_jsonStringifier.Stringify(new {
                    Url = VirtualPathUtility.ToAbsolute(context.Request.AppRelativeCurrentExecutionFilePath.Replace("/negotiate", "")),
                    ClientId = Guid.NewGuid().ToString("d")
                }));
            }
            else {
                var contextBase = new HttpContextWrapper(context);
                _transport = GetTransport(contextBase);

                string clientId = contextBase.Request["clientId"];
                IEnumerable<string> groups = GetGroups(contextBase);

                Connection = CreateConnection(clientId, groups, contextBase);

                // Wire up the events we needs
                _transport.Connected += () => {
                    OnConnected(contextBase, clientId);
                };

                _transport.Received += (data) => {
                    task = OnReceivedAsync(clientId, data);
                };

                _transport.Error += (e) => {
                    OnError(e);
                };

                _transport.Disconnected += () => {
                    OnDisconnect(clientId);
                };

                var processRequestTask = _transport.ProcessRequest(Connection);

                if (processRequestTask != null) {
                    return processRequestTask;
                }
            }

            return task ?? TaskAsyncHelper.Empty;
        }

        protected virtual IConnection CreateConnection(string clientId, IEnumerable<string> groups, HttpContextBase context) {
            string groupValue = context.Request["groups"] ?? String.Empty;

            // The list of default signals this connection cares about:
            // 1. The default signal (the type name)
            // 2. The client id (so we can message this particular connection)
            // 3. client id + SIGNALRCOMMAND -> for built in commands that we need to process
            var signals = new string[] {
                DefaultSignal,
                clientId,
                clientId + "." + SignalrCommand
            };

            return new Connection(_store, _signaler, DefaultSignal, clientId, signals, groups);
        }

        protected virtual void OnConnected(HttpContextBase context, string clientId) { }

        protected virtual Task OnReceivedAsync(string clientId, string data) {
            OnReceived(clientId, data);
            return TaskAsyncHelper.Empty;
        }

        protected virtual void OnReceived(string clientId, string data) { }

        protected virtual void OnDisconnect(string clientId) { }

        protected virtual void OnError(Exception e) { }

        public void Send(object value) {
            _transport.Send(value);
        }

        public void Send(string clientId, object value) {
            Connection.Broadcast(clientId, value);
        }

        public void SendToGroup(string groupName, object value) {
            Connection.Broadcast(CreateQualifiedName(groupName), value);
        }

        public void AddToGroup(string clientId, string groupName) {
            groupName = CreateQualifiedName(groupName);
            SendCommand(clientId, CommandType.AddToGroup, groupName);
        }

        public void RemoveFromGroup(string clientId, string groupName) {
            groupName = CreateQualifiedName(groupName);
            SendCommand(clientId, CommandType.RemoveFromGroup, groupName);
        }

        private void SendCommand(string clientId, CommandType type, object value) {
            string signal = clientId + "." + SignalrCommand;

            var groupCommand = new SignalCommand {
                Type = type,
                Value = value
            };

            Connection.Broadcast(signal, groupCommand);
        }

        private string CreateQualifiedName(string groupName) {
            return DefaultSignal + "." + groupName;
        }

        private IEnumerable<string> GetGroups(HttpContextBase context) {
            string groupValue = context.Request["groups"];

            if (String.IsNullOrEmpty(groupValue)) {
                return Enumerable.Empty<string>();
            }

            return groupValue.Split(',');
        }

        private bool IsNegotiationRequest(HttpRequest httpRequest) {
            return httpRequest.Path.EndsWith("/negotiate", StringComparison.OrdinalIgnoreCase);
        }

        private ITransport GetTransport(HttpContextBase context) {
            return TransportManager.GetTransport(context) ?? 
                   new LongPollingTransport(context, _jsonStringifier);
        }
    }
}