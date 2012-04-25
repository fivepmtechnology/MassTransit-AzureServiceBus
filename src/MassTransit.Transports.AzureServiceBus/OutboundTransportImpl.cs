// Copyright 2012 Henrik Feldt
//  
// Licensed under the Apache License, Version 2.0 (the "License"); you may not use 
// this file except in compliance with the License. You may obtain a copy of the 
// License at 
// 
//     http://www.apache.org/licenses/LICENSE-2.0 
// 
// Unless required by applicable law or agreed to in writing, software distributed 
// under the License is distributed on an "AS IS" BASIS, WITHOUT WARRANTIES OR 
// CONDITIONS OF ANY KIND, either express or implied. See the License for the 
// specific language governing permissions and limitations under the License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Magnum.Extensions;
using Magnum.Threading;
using MassTransit.AzureServiceBus;
using MassTransit.AzureServiceBus.Util;
using MassTransit.Logging;
using MassTransit.Transports.AzureServiceBus.Internal;
using Microsoft.ServiceBus.Messaging;
using MessageSender = MassTransit.AzureServiceBus.MessageSender;

#pragma warning disable 1591

namespace MassTransit.Transports.AzureServiceBus
{
	/// <summary>
	/// 	Outbound transport targeting the azure service bus.
	/// </summary>
	public class OutboundTransportImpl
		: IOutboundTransport
	{
		const int MaxOutstanding = 100;
		const string BusyRetriesKey = "busy-retries";
		static readonly ILog _logger = Logger.Get(typeof (OutboundTransportImpl));

		bool _disposed;

		int _messagesInFlight;
		int _sleeping;

		readonly ReaderWriterLockedObject<Queue<BrokeredMessage>> _retryMsgs
			= new ReaderWriterLockedObject<Queue<BrokeredMessage>>(new Queue<BrokeredMessage>(MaxOutstanding));

		readonly ConnectionHandler<ConnectionImpl> _connectionHandler;
		readonly AzureServiceBusEndpointAddress _address;

		/// <summary>
		/// 	c'tor
		/// </summary>
		public OutboundTransportImpl(
			[NotNull] AzureServiceBusEndpointAddress address,
			[NotNull] ConnectionHandler<ConnectionImpl> connectionHandler)
		{
			if (address == null) throw new ArgumentNullException("address");
			if (connectionHandler == null) throw new ArgumentNullException("connectionHandler");

			_connectionHandler = connectionHandler;
			_address = address;

			_logger.DebugFormat("created outbound transport for address '{0}'", address);
		}

		public void Dispose()
		{
			if (_disposed) return;
			try
			{
				_address.Dispose();
				_connectionHandler.Dispose();
			}
			finally
			{
				_disposed = true;
			}
		}

		/// <summary>
		/// 	Gets the endpoint address this transport sends to.
		/// </summary>
		public IEndpointAddress Address
		{
			get { return _address; }
		}

		// service bus best practices for performance:
		// http://msdn.microsoft.com/en-us/library/windowsazure/hh528527.aspx
		public void Send(ISendContext context)
		{
			_connectionHandler
				.Use(connection =>
					{
						using (var body = new MemoryStream())
						{
							context.SerializeTo(body);
							var bm = new BrokeredMessage(new MessageEnvelope(body.ToArray()));

							if (!string.IsNullOrWhiteSpace(context.CorrelationId))
								bm.CorrelationId = context.CorrelationId;

							if (!string.IsNullOrWhiteSpace(context.MessageId))
								bm.MessageId = context.MessageId;

							TrySendMessage(connection, bm);
						}
					});
		}

		void TrySendMessage(ConnectionImpl connection, BrokeredMessage message)
		{
			// don't have too many outstanding at same time
			SpinWait.SpinUntil(() => _messagesInFlight < MaxOutstanding);

			Address.LogBeginSend(message.MessageId);

			Interlocked.Increment(ref _messagesInFlight);

			var sendTask = Task.Factory.FromAsync(connection.MessageSender.BeginSend, connection.MessageSender.EndSend, message, null)
			.ContinueWith(t =>
				{
					Interlocked.Decrement(ref _messagesInFlight);
					
					// if the queue is deleted in the middle of things here, then I can't recover
					// at the moment; I have to extend the connection handler with an asynchronous
					// API to let it re-initialize the queue and hence maybe even the full transport...
					// MessagingEntityNotFoundException.
					try
					{
						try
						{
							t.Wait();
						}
						catch (AggregateException ae)
						{
							throw ae.InnerException;
						}
					}
					catch (ServerBusyException ex)
					{
						_logger.Warn(string.Format("server busy, retrying for msg #{0}", message.MessageId), ex);
					}
					catch (MessagingCommunicationException ex)
					{
						_logger.Warn(string.Format("server sad, retrying for msg #{0}", message.MessageId), ex);
					}
					catch (Exception ex)
					{
						_logger.Error(string.Format("other exception for msg #{0}", message.MessageId), ex);
					}

					Address.LogEndSend(message.MessageId);

					// success
					if (t.Exception == null) return;

					// schedule retry
					var retries = UpdateRetries(message);
					_logger.WarnFormat("scheduling retry no. {0} for msg #{1} ", retries, message.MessageId);

					RetryLoop(connection, message);
				});

			// optionally block here
			// sendTask.Wait();
		}


		static int UpdateRetries(BrokeredMessage msg)
		{
			object val;
			var hasVal = msg.Properties.TryGetValue(BusyRetriesKey, out val);
			if (!hasVal) val = msg.Properties[BusyRetriesKey] = 1;
			else val = msg.Properties[BusyRetriesKey] = (int) val + 1;
			return (int) val;
		}

		// call only if first time gotten server busy exception
		void RetryLoop(ConnectionImpl connection, BrokeredMessage bm)
		{
			Address.LogSendRetryScheduled(bm.MessageId, _messagesInFlight, Interlocked.Increment(ref _sleeping));
			// exception tells me to wait 10 seconds before retrying, so let's sleep 1 second instead,
			// just 2,600,000,000 CPU cycles
			Thread.Sleep(1.Seconds());
			Interlocked.Decrement(ref _sleeping);

			// push all pending retries onto the sending operation
			_retryMsgs.WriteLock(queue =>
				{
					queue.Enqueue(bm);
					while (queue.Count > 0)
						TrySendMessage(connection, queue.Dequeue());
				});
		}
	}
}