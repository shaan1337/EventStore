using System;
using System.Collections.Generic;
using System.Linq;
using EventStore.Core.Messages;
using EventStore.Core.Messaging;
using EventStore.Projections.Core.Messages;
using EventStore.Projections.Core.Services;
using EventStore.Projections.Core.Services.Management;
using NUnit.Framework;

namespace EventStore.Projections.Core.Tests.Services.projections_manager
{
    [TestFixture]
    public class when_posting_a_persistent_projection_and_registration_write_fails : TestFixtureWithProjectionCoreAndManagementServices
    {
        protected override void Given()
        {
            NoStream("$projections-test-projection-order");
            AllWritesToSucceed("$projections-test-projection-order");
            NoStream("$projections-test-projection-checkpoint");
            NoOtherStreams();
            AllWritesQueueUp();
        }

        private string _projectionName;

        protected override IEnumerable<WhenStep> When()
        {
            _projectionName = "test-projection";
            yield return new SystemMessage.BecomeMaster(Guid.NewGuid());
            yield return new SystemMessage.SystemCoreReady();
            yield return
                new ProjectionManagementMessage.Command.Post(
                    new PublishEnvelope(_bus), ProjectionMode.Continuous, _projectionName,
                    ProjectionManagementMessage.RunAs.System, "JS", @"fromAll().when({$any:function(s,e){return s;}});",
                    enabled: true, checkpointsEnabled: true, emitEnabled: true, trackEmittedStreams: true);
        }

        [Test, Category("v8")]
        public void retries_creating_the_projection_only_the_specified_number_of_times()
        {
            int retryCount = 0;
            var projectionRegistrationWrite = _consumer.HandledMessages.OfType<ClientMessage.WriteEvents>().Where(x => x.EventStreamId == "$projections-$all").Last();
            while (projectionRegistrationWrite != null)
            {
                projectionRegistrationWrite.Envelope.ReplyWith(new ClientMessage.WriteEventsCompleted(projectionRegistrationWrite.CorrelationId, OperationResult.CommitTimeout, "Commit Timeout"));
                _queue.Process();
                projectionRegistrationWrite = _consumer.HandledMessages.OfType<ClientMessage.WriteEvents>().Where(x => x.EventStreamId == "$projections-$all").LastOrDefault();
                if(projectionRegistrationWrite != null)
                {
                    retryCount++;
                }
                _consumer.HandledMessages.Clear();
            }
            Assert.AreEqual(ProjectionManager.ProjectionCreationRetryCount, retryCount);
        }
    }
}
