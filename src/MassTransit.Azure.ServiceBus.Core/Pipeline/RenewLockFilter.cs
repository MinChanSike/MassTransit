﻿// Copyright 2007-2017 Chris Patterson, Dru Sellers, Travis Smith, et. al.
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
namespace MassTransit.Azure.ServiceBus.Core.Pipeline
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using GreenPipes;
    using Logging;
    using Microsoft.Azure.ServiceBus;
    using Microsoft.Azure.ServiceBus.Core;


    public class RenewLockFilter :
        IFilter<ConsumeContext>
    {
        static readonly ILog _log = Logger.Get<RenewLockFilter>();

        readonly TimeSpan _delay;

        public RenewLockFilter(TimeSpan delay)
        {
            _delay = delay;
        }

        public async Task Send(ConsumeContext context, IPipe<ConsumeContext> next)
        {
            context.TryGetPayload(out BrokeredMessageContext brokeredMessageContext);
            context.TryGetPayload(out IMessageReceiver messageReceiver);

            var scope = new RenewLockScope(messageReceiver, brokeredMessageContext, _delay);
            try
            {
                await next.Send(context).ConfigureAwait(false);
            }
            finally
            {
                await scope.Complete().ConfigureAwait(false);
            }
        }

        public void Probe(ProbeContext context)
        {
            context.CreateFilterScope("renewLock");
        }


        class RenewLockScope
        {
            readonly TaskCompletionSource<bool> _completed;
            readonly IMessageReceiver _messageReceiver;
            readonly BrokeredMessageContext _context;
            readonly TimeSpan _delay;
            readonly CancellationTokenSource _source;

            public RenewLockScope(IMessageReceiver messageReceiver, BrokeredMessageContext context, TimeSpan delay)
            {
                _messageReceiver = messageReceiver;
                _context = context;
                _delay = delay;
                _source = new CancellationTokenSource();
                _completed = new TaskCompletionSource<bool>();

                if (context != null)
                {
                    Task.Factory.StartNew(LockRenewal, CancellationToken.None, TaskCreationOptions.None, TaskScheduler.Default);
                }
            }

            async Task LockRenewal()
            {
                var delay = _delay;

                if (LockWouldExpireBeforeRenewal())
                {
                    delay = TimeSpan.Zero;
                }

                while (_source.Token.IsCancellationRequested == false)
                {
                    try
                    {
                        await Task.Delay(delay, _source.Token).ConfigureAwait(false);

                        if (_source.Token.IsCancellationRequested)
                            break;

                        await _messageReceiver.RenewLockAsync(_context.LockToken).ConfigureAwait(false);

                        if (_log.IsDebugEnabled)
                            _log.DebugFormat("Renewed Lock: {0}", _context.MessageId);

                        delay = _delay;
                    }
                    catch (MessageLockLostException exception)
                    {
                        _source.Cancel();
                        _completed.TrySetException(exception);

                        if (_log.IsWarnEnabled)
                            _log.Warn($"Lost Message Lock: {_context.MessageId}", exception);
                    }
                    catch (SessionLockLostException exception)
                    {
                        _source.Cancel();
                        _completed.TrySetException(exception);

                        if (_log.IsWarnEnabled)
                            _log.Warn($"Lost Message Lock: {_context.MessageId}", exception);
                    }
                    catch (OperationCanceledException)
                    {
                    }
                    catch (TimeoutException exception)
                    {
                        delay = TimeSpan.Zero;

                        if (_log.IsWarnEnabled)
                            _log.Warn($"Renew Lock Timeout (will retry): {_context.MessageId}", exception);
                    }
                    catch (Exception exception)
                    {
                        _source.Cancel();
                        _completed.TrySetException(exception);

                        if (_log.IsWarnEnabled)
                            _log.Warn($"Renew Lock Exception: {_context.MessageId}", exception);
                    }
                }

                _completed.TrySetResult(true);
            }

            bool LockWouldExpireBeforeRenewal()
            {
                return DateTime.UtcNow + _delay >= _context.LockedUntil;
            }

            public Task Complete()
            {
                _source.Cancel();

                return _completed.Task;
            }
        }
    }
}