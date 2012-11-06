// Copyright (c) 2012, Event Store LLP
// All rights reserved.
// 
// Redistribution and use in source and binary forms, with or without
// modification, are permitted provided that the following conditions are
// met:
// 
// Redistributions of source code must retain the above copyright notice,
// this list of conditions and the following disclaimer.
// Redistributions in binary form must reproduce the above copyright
// notice, this list of conditions and the following disclaimer in the
// documentation and/or other materials provided with the distribution.
// Neither the name of the Event Store LLP nor the names of its
// contributors may be used to endorse or promote products derived from
// this software without specific prior written permission
// THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
// "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT
// LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR
// A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT
// HOLDER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL,
// SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT
// LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE,
// DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY
// THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
// (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
// OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
// 

using System;
using EventStore.Common.Log;
using EventStore.Core.Bus;
using EventStore.Core.Data;
using EventStore.Core.Messages;
using EventStore.Core.Messaging;
using EventStore.Core.Services.TimerService;
using EventStore.Core.TransactionLog.LogRecords;
using EventStore.Projections.Core.Messages;

namespace EventStore.Projections.Core.Services.Processing
{
    public class TransactionFileReaderEventDistributionPoint : EventDistributionPoint
    {
        private readonly ILogger _logger = LogManager.GetLoggerFor<TransactionFileReaderEventDistributionPoint>();

        private bool _paused = true;
        private bool _pauseRequested = true;
        private bool _eventsRequested = false;
        private int _maxReadCount = 50;
        private bool _disposed;
        private EventPosition _from;
        private readonly bool _deliverEndOfTfPosition;

        public TransactionFileReaderEventDistributionPoint(
            IPublisher publisher, Guid distibutionPointCorrelationId, EventPosition from, bool deliverEndOfTFPosition = true)
            : base(publisher, distibutionPointCorrelationId)
        {
            if (publisher == null) throw new ArgumentNullException("publisher");
            _from = @from;
            _deliverEndOfTfPosition = deliverEndOfTFPosition;
        }

        public override void Resume()
        {
            if (_disposed) throw new InvalidOperationException("Disposed");
            if (!_pauseRequested)
                throw new InvalidOperationException("Is not paused");
            if (!_paused)
            {
                _pauseRequested = false;
                return;
            }
            
            _paused = false;
            _pauseRequested = false;
            _logger.Trace("Resuming event distribution {0} at '{1}'", _distibutionPointCorrelationId, _from);
            RequestEvents(delay: false);
        }

        public override void Pause()
        {
            if (_disposed) throw new InvalidOperationException("Disposed");
            if (_pauseRequested)
                throw new InvalidOperationException("Pause has been already requested");
            _pauseRequested = true;
            if (!_eventsRequested)
                _paused = true;
            _logger.Trace("Pausing event distribution {0} at '{1}", _distibutionPointCorrelationId, _from);
        }

        public override void Handle(ClientMessage.ReadStreamEventsForwardCompleted message)
        {
        }

        public override void Handle(ClientMessage.ReadAllEventsForwardCompleted message)
        {
            if (_disposed)
                return;
            if (!_eventsRequested)
                throw new InvalidOperationException("Read events has not been requested");
            if (_paused)
                throw new InvalidOperationException("Paused");
            _eventsRequested = false;

            if (message.Result.Records.Length == 0)
            {
                // the end
                if (_deliverEndOfTfPosition)
                    DeliverLastCommitPosition(_from);
                // allow joining heading distribution
            }
            else
            {
                for (int index = 0; index < message.Result.Records.Length; index++)
                {
                    var @event = message.Result.Records[index];
                    DeliverEvent(@event, message.Result.TfEofPosition);
                }
                _from = message.Result.NextPos;
            }


            if (_pauseRequested)
                _paused = true;
            else if (message.Result.Records.Length == 0)
                RequestEvents(delay: true);
            else
                _publisher.Publish(CreateTickMessage());

        }

        private ProjectionMessage.CoreService.Tick CreateTickMessage()
        {
            return
                new ProjectionMessage.CoreService.Tick(
                    () => { if (!_paused && !_disposed) RequestEvents(delay: false); });
        }

        private void RequestEvents(bool delay)
        {
            if (_disposed) throw new InvalidOperationException("Disposed");
            if (_eventsRequested)
                throw new InvalidOperationException("Read operation is already in progress");
            if (_pauseRequested || _paused)
                throw new InvalidOperationException("Paused or pause requested");
            _eventsRequested = true;


            var readEventsForward = new ClientMessage.ReadAllEventsForward(
                    _distibutionPointCorrelationId, new SendToThisEnvelope(this), _from.CommitPosition,
                    _from.PreparePosition == -1 ? _from.CommitPosition : _from.PreparePosition, _maxReadCount, true);
            if (delay)
                _publisher.Publish(
                    TimerMessage.Schedule.Create(
                        TimeSpan.FromMilliseconds(250), new PublishEnvelope(_publisher, crossThread: true),
                        readEventsForward));
            else
                _publisher.Publish(readEventsForward);
        }

        public override void Dispose()
        {
            _disposed = true;
        }

        private void DeliverLastCommitPosition(EventPosition lastPosition)
        {
            _publisher.Publish(
                new ProjectionMessage.Projections.CommittedEventDistributed(
                    _distibutionPointCorrelationId, default(EventPosition), null, int.MinValue,
                    null, int.MinValue, false, null, lastPosition.PreparePosition, 100.0f)); //TODO: check was is passed here
        }

        private void DeliverEvent(ResolvedEventRecord @event, long lastCommitPosition)
        {
            EventRecord positionEvent = (@event.Link ?? @event.Event);
            var receivedPosition = new EventPosition(@event.CommitPosition, positionEvent.LogPosition);
            if (_from > receivedPosition)
                throw new Exception(
                    string.Format(
                        "ReadFromTF returned events in incorrect order.  Last known position is: {0}.  Received position is: {1}",
                        _from, receivedPosition));

            _publisher.Publish(
                new ProjectionMessage.Projections.CommittedEventDistributed(
                    _distibutionPointCorrelationId, receivedPosition, positionEvent.EventStreamId,
                    positionEvent.EventNumber, @event.Event.EventStreamId, @event.Event.EventNumber, @event.Link != null,
                    new Event(
                        @event.Event.EventId, @event.Event.EventType, (@event.Event.Flags & PrepareFlags.IsJson) != 0,
                        @event.Event.Data, @event.Event.Metadata), receivedPosition.PreparePosition, 100.0f * positionEvent.LogPosition / lastCommitPosition));
        }
    }
}
