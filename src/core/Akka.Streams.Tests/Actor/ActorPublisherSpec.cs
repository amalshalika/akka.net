﻿using System;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Streams;
using System.Threading;
using Akka.Actor;
using Akka.Pattern;
using Akka.Streams.Actors;
using Akka.Streams.Dsl;
using Akka.Streams.TestKit;
using Akka.Streams.TestKit.Tests;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace Akka.Streams.Tests.Actor
{
    public class ActorPublisherSpec : AkkaSpec
    {
        private const string Config = @"
my-dispatcher1 {
  type = Dispatcher
  executor = ""fork-join-executor""
  fork-join-executor {
    parallelism-min = 8
    parallelism-max = 8
  }
  mailbox-requirement = ""Akka.Dispatch.IUnboundedMessageQueueSemantics""
}
my-dispatcher1 {
  type = Dispatcher
  executor = ""fork-join-executor""
  fork-join-executor {
    parallelism-min = 8
    parallelism-max = 8
  }
  mailbox-requirement = ""Akka.Dispatch.IUnboundedMessageQueueSemantics""
}";

        public ActorPublisherSpec(ITestOutputHelper output = null) : base(Config, output)
        {
            EventFilter.Exception<IllegalStateException>().Mute();
        }

        [Fact]
        public void ActorPublisher_should_accumulate_demand()
        {
            var probe = CreateTestProbe();
            var actorRef = Sys.ActorOf(TestPublisher.Props(probe.Ref));
            var p = ActorPublisher.Create<string>(actorRef);
            var s = this.CreateProbe<string>();

            p.Subscribe(s);
            s.Request(2);
            probe.ExpectMsg<TotalDemand>().Elements.Should().Be(2);
            s.Request(3);
            probe.ExpectMsg<TotalDemand>().Elements.Should().Be(5);
            s.Cancel();
        }

        [Fact]
        public void ActorPublisher_should_allow_onNext_up_to_requested_elements_but_not_more()
        {
            var probe = CreateTestProbe();
            var actorRef = Sys.ActorOf(TestPublisher.Props(probe.Ref));
            var p = ActorPublisher.Create<string>(actorRef);
            var s = this.CreateProbe<string>();
            p.Subscribe(s);
            s.Request(2);
            actorRef.Tell(new Produce("elem-1"));
            actorRef.Tell(new Produce("elem-2"));
            actorRef.Tell(new Produce("elem-3"));
            s.ExpectNext("elem-1");
            s.ExpectNext("elem-2");
            s.ExpectNoMsg(TimeSpan.FromMilliseconds(300));
            s.Cancel();
        }

        [Fact]
        public void ActorPublisher_should_signal_error()
        {
            var probe = CreateTestProbe();
            var actorRef = Sys.ActorOf(TestPublisher.Props(probe.Ref));
            var s = this.CreateManualProbe<string>();
            ActorPublisher.Create<string>(actorRef).Subscribe(s);
            actorRef.Tell(new Err("wrong"));
            s.ExpectSubscription();
            s.ExpectError().Message.Should().Be("wrong");
        }

        [Fact]
        public void ActorPublisher_should_not_terminate_after_signaling_onError()
        {
            var probe = CreateTestProbe();
            var actorRef = Sys.ActorOf(TestPublisher.Props(probe.Ref));
            var s = this.CreateManualProbe<string>();
            ActorPublisher.Create<string>(actorRef).Subscribe(s);
            s.ExpectSubscription();
            probe.Watch(actorRef);
            actorRef.Tell(new Err("wrong"));
            s.ExpectError().Message.Should().Be("wrong");
            probe.ExpectNoMsg(TimeSpan.FromMilliseconds(200));
        }

        [Fact]
        public void ActorPublisher_should_terminate_after_signalling_OnErrorThenStop()
        {
            var probe = CreateTestProbe();
            var actorRef = Sys.ActorOf(TestPublisher.Props(probe.Ref));
            var s = this.CreateManualProbe<string>();
            ActorPublisher.Create<string>(actorRef).Subscribe(s);
            s.ExpectSubscription();
            probe.Watch(actorRef);
            actorRef.Tell(new ErrThenStop("wrong"));
            s.ExpectError().Message.Should().Be("wrong");
            probe.ExpectTerminated(actorRef, TimeSpan.FromSeconds(3));
        }

        [Fact]
        public void ActorPublisher_should_signal_error_before_subscribe()
        {
            var probe = CreateTestProbe();
            var actorRef = Sys.ActorOf(TestPublisher.Props(probe.Ref));
            actorRef.Tell(new Err("early err"));
            var s = this.CreateManualProbe<string>();
            ActorPublisher.Create<string>(actorRef).Subscribe(s);
            s.ExpectSubscriptionAndError().Message.Should().Be("early err");
        }

        [Fact]
        public void ActorPublisher_should_drop_onNext_elements_after_cancel()
        {
            var probe = CreateTestProbe();
            var actorRef = Sys.ActorOf(TestPublisher.Props(probe.Ref));
            var p = ActorPublisher.Create<string>(actorRef);
            var s = this.CreateProbe<string>();
            p.Subscribe(s);
            s.Request(2);
            actorRef.Tell(new Produce("elem-1"));
            s.Cancel();
            actorRef.Tell(new Produce("elem-2"));
            s.ExpectNext("elem-1");
            s.ExpectNoMsg(TimeSpan.FromMilliseconds(300));
        }

        [Fact]
        public void ActorPublisher_should_remember_requested_after_restart()
        {
            var probe = CreateTestProbe();
            var actorRef = Sys.ActorOf(TestPublisher.Props(probe.Ref));
            var p = ActorPublisher.Create<string>(actorRef);
            var s = this.CreateProbe<string>();
            p.Subscribe(s);
            s.Request(3);
            probe.ExpectMsg<TotalDemand>().Elements.Should().Be(3);
            actorRef.Tell(new Produce("elem-1"));
            actorRef.Tell(Boom.Instance);
            actorRef.Tell(new Produce("elem-2"));
            s.ExpectNext("elem-1");
            s.ExpectNext("elem-2");
            s.Request(5);
            probe.ExpectMsg<TotalDemand>().Elements.Should().Be(6);
            s.Cancel();
        }

        [Fact]
        public void ActorPublisher_should_signal_onComplete()
        {
            var probe = CreateTestProbe();
            var actorRef = Sys.ActorOf(TestPublisher.Props(probe.Ref));
            var s = this.CreateProbe<string>();
            ActorPublisher.Create<string>(actorRef).Subscribe(s);
            s.Request(3);
            actorRef.Tell(new Produce("elem-1"));
            actorRef.Tell(Complete.Instance);
            s.ExpectNext("elem-1");
            s.ExpectComplete();
        }

        [Fact]
        public void ActorPublisher_should_not_terminate_after_signalling_onComplete()
        {
            var probe = CreateTestProbe();
            var actorRef = Sys.ActorOf(TestPublisher.Props(probe.Ref));
            var s = this.CreateProbe<string>();
            ActorPublisher.Create<string>(actorRef).Subscribe(s);
            var sub = s.ExpectSubscription();
            sub.Request(3);
            probe.ExpectMsg<TotalDemand>().Elements.Should().Be(3);
            probe.Watch(actorRef);
            actorRef.Tell(new Produce("elem-1"));
            actorRef.Tell(Complete.Instance);
            s.ExpectNext("elem-1");
            s.ExpectComplete();
            probe.ExpectNoMsg(TimeSpan.FromMilliseconds(200));
        }

        [Fact]
        public void ActorPublisher_should_terminate_after_signalling_onCompleteThenStop()
        {
            var probe = CreateTestProbe();
            var actorRef = Sys.ActorOf(TestPublisher.Props(probe.Ref));
            var s = this.CreateProbe<string>();
            ActorPublisher.Create<string>(actorRef).Subscribe(s);
            var sub = s.ExpectSubscription();
            sub.Request(3);
            probe.ExpectMsg<TotalDemand>().Elements.Should().Be(3);
            probe.Watch(actorRef);
            actorRef.Tell(new Produce("elem-1"));
            actorRef.Tell(CompleteThenStop.Instance);
            s.ExpectNext("elem-1");
            s.ExpectComplete();
            probe.ExpectTerminated(actorRef,TimeSpan.FromSeconds(3));
        }

        [Fact]
        public void ActorPublisher_should_singal_immediate_onComplete()
        {
            var probe = CreateTestProbe();
            var actorRef = Sys.ActorOf(TestPublisher.Props(probe.Ref));
            actorRef.Tell(Complete.Instance);
            var s = this.CreateManualProbe<string>();
            ActorPublisher.Create<string>(actorRef).Subscribe(s);
            s.ExpectSubscriptionAndComplete();
        }

        [Fact]
        public void ActorPublisher_should_only_allow_one_subscriber()
        {
            var probe = CreateTestProbe();
            var actorRef = Sys.ActorOf(TestPublisher.Props(probe.Ref));
            var s = this.CreateManualProbe<string>();
            ActorPublisher.Create<string>(actorRef).Subscribe(s);
            s.ExpectSubscription();
            var s2 = this.CreateManualProbe<string>();
            ActorPublisher.Create<string>(actorRef).Subscribe(s2);
            s2.ExpectSubscriptionAndError().Should().BeOfType<IllegalStateException>();
        }

        [Fact]
        public void ActorPublisher_should_signal_onComplete_when_actor_is_stopped()
        {
            var probe = CreateTestProbe();
            var actorRef = Sys.ActorOf(TestPublisher.Props(probe.Ref));
            var s = this.CreateManualProbe<string>();
            ActorPublisher.Create<string>(actorRef).Subscribe(s);
            s.ExpectSubscription();
            actorRef.Tell(PoisonPill.Instance);
            s.ExpectComplete();
        }

        [Fact]
        public void ActorPublisher_should_work_together_with_Flow_and_ActorSubscriber()
        {
            var materializer = Sys.Materializer();
            this.AssertAllStagesStopped(() =>
            {
                var probe = CreateTestProbe();
                var source = Source.ActorPublisher<int>(Sender.Props);
                var sink = Sink.ActorSubscriber<string>(Receiver.Props(probe.Ref));
                
                var t = source.Collect(n =>
                {
                    if (n%2 == 0)
                        return "elem-" + n;
                    return null;
                }).ToMaterialized(sink, Keep.Both).Run(materializer);
                var snd = t.Item1;
                var rcv = t.Item2;

                for (var i = 1; i <= 3; i++)
                    snd.Tell(i);
                probe.ExpectMsg("elem-2");

                for (var n = 4; n <= 500; n++)
                {
                    if (n%19 == 0)
                        Thread.Sleep(50); // simulate bursts
                    snd.Tell(n);
                }

                for (var n = 4; n <= 500; n += 2)
                    probe.ExpectMsg("elem-" + n);

                Watch(snd);
                rcv.Tell(PoisonPill.Instance);
                ExpectTerminated(snd);
            }, materializer);
        }


        [Fact]
        public void ActorPublisher_should_work_in_a_GraphDsl()
        {
            var materializer = Sys.Materializer();
            var probe1 = CreateTestProbe();
            var probe2 = CreateTestProbe();

            var senderRef1 = ActorOf(Sender.Props);
            var source1 = Source.FromPublisher<int, IActorRef>(ActorPublisher.Create<int>(senderRef1));

            var sink1 = Sink.FromSubscriber<string, IActorRef>(ActorSubscriber.Create<string>(ActorOf(Receiver.Props(probe1.Ref))));
            var sink2 = Sink.ActorSubscriber<string>(Receiver.Props(probe2.Ref));
            var senderRef2 = RunnableGraph<IActorRef>.FromGraph(GraphDsl.Create(
                Source.ActorPublisher<int>(Sender.Props),
                (builder, source2) =>
                {
                    var merge = builder.Add(new Merge<int, int>(2));
                    var bcast = builder.Add(new Broadcast<string>(2));

                    builder.From(source1).To(merge.In(0));
                    builder.From(source2.Outlet).To(merge.In(1));
                    
                    builder.From(merge.Out).Via(Flow.Create<int>().Map(i => i.ToString())).To(bcast.In);
                    
                    builder.From(bcast.Out(0)).Via(Flow.Create<string>().Map(s => s + "mark")).To(sink1);
                    builder.From(bcast.Out(1)).To(sink2);

                    return ClosedShape.Instance;
                })).Run(materializer);

            for (var i = 0; i <= 10; i++)
            {
                senderRef1.Tell(i);
                senderRef2.Tell(i);
            }

            for (var i = 0; i <= 10; i++)
            {
                probe1.ExpectMsg(i + "mark");
                probe2.ExpectMsg(i.ToString());
            }
        }

        [Fact]
        public void ActorPublisher_should_be_able_to_define_a_subscription_timeout_after_which_it_should_shut_down()
        {
            var materializer = Sys.Materializer();
            this.AssertAllStagesStopped(() =>
            {
                var timeout = TimeSpan.FromMilliseconds(150);
                var a = ActorOf(TimeoutingPublisher.Props(TestActor, timeout));
                var pub = ActorPublisher.Create<int>(a);

                // don't subscribe for `timeout` millis, so it will shut itself down
                ExpectMsg("timed-out");

                // now subscribers will already be rejected, while the actor could perform some clean-up
                var sub = this.CreateManualProbe<int>();
                pub.Subscribe(sub);
                sub.ExpectSubscriptionAndError();

                ExpectMsg("cleaned-up");
                // termination is tiggered by user code
                Watch(a);
                ExpectTerminated(a);
            }, materializer);
        }

        [Fact]
        public void ActorPublisher_should_be_able_to_define_a_subscription_timeout_which_is_cancelled_by_the_first_incoming_Subscriber()
        {
            var timeout = TimeSpan.FromMilliseconds(500);
            var sub = this.CreateManualProbe<int>();

            var pub = ActorPublisher.Create<int>(ActorOf(TimeoutingPublisher.Props(TestActor, timeout)));

            // subscribe right away, should cancel subscription-timeout
            pub.Subscribe(sub);
            sub.ExpectSubscription();

            ExpectNoMsg(TimeSpan.FromSeconds(1));
        }

        [Fact]
        public void ActorPublisher_should_use_dispatcher_from_materializer_settings()
        {
            var materializer = ActorMaterializer.Create(Sys, Sys.Materializer().Settings.WithDispatcher("my-dispatcher1"));
            var s = this.CreateManualProbe<string>();
            var actorRef = Source.ActorPublisher<string>(TestPublisher.Props(TestActor, useTestDispatcher: false))
                    .To(Sink.FromSubscriber<string, Unit>(s))
                    .Run(materializer);

            actorRef.Tell(ThreadName.Instance);
            ExpectMsg<string>().Should().Contain("my-dispatcher1");
        }

        [Fact]
        public void ActorPublisher_should_use_dispatcher_from_operation_attributes()
        {
            var materializer = Sys.Materializer();
            var s = this.CreateManualProbe<string>();
            var actorRef = Source.ActorPublisher<string>(TestPublisher.Props(TestActor, useTestDispatcher: false))
                .WithAttributes(ActorAttributes.CreateDispatcher("my-dispatcher1"))
                .To(Sink.FromSubscriber<string, Unit>(s))
                .Run(materializer);

            actorRef.Tell(ThreadName.Instance);
            ExpectMsg<string>().Should().Contain("my-dispatcher1");
        }

        [Fact]
        public void ActorPublisher_should_use_dispatcher_from_props()
        {
            var materializer = Sys.Materializer();
            var s = this.CreateManualProbe<string>();
            var actorRef = Source.ActorPublisher<string>(TestPublisher.Props(TestActor, useTestDispatcher: false).WithDispatcher("my-dispatcher1"))
                .WithAttributes(ActorAttributes.CreateDispatcher("my-dispatcher2"))
                .To(Sink.FromSubscriber<string, Unit>(s))
                .Run(materializer);

            actorRef.Tell(ThreadName.Instance);
            ExpectMsg<string>().Should().Contain("my-dispatcher1");
        }
    }

    internal class TestPublisher : ActorPublisher<string>
    {
        public static Props Props(IActorRef probe, bool useTestDispatcher = true)
        {
            var p = Akka.Actor.Props.Create(() => new TestPublisher(probe));
            return useTestDispatcher ? p.WithDispatcher("akka.test.stream-dispatcher") : p;
        }

        private readonly IActorRef _probe;
        
        public TestPublisher(IActorRef probe)
        {
            _probe = probe;
        }

        protected override bool Receive(object message)
        {
            return message.Match()
                .With<Request>(request => _probe.Tell(new TotalDemand(TotalDemand)))
                .With<Produce>(produce => OnNext(produce.Elem))
                .With<Err>(err => OnError(new SystemException(err.Reason)))
                .With<ErrThenStop>(err => OnErrorThenStop(new SystemException(err.Reason)))
                .With<Complete>(OnComplete)
                .With<CompleteThenStop>(OnCompleteThenStop)
                .With<Boom>(() => { throw new SystemException("boom"); })
                .With<ThreadName>(()=>_probe.Tell(Context.Props.Dispatcher /*Thread.CurrentThread.Name*/)) // TODO fix me when thread name is set by dispatcher
                .WasHandled;
        }
    }

    internal class Sender : ActorPublisher<int>
    {
        public static Props Props { get; } = Props.Create<Sender>().WithDispatcher("akka.test.stream-dispatcher");

        private IImmutableList<int> _buffer = ImmutableList<int>.Empty;

        protected override bool Receive(object message)
        {
            return message.Match()
                .With<int>(i =>
                {
                    if (_buffer.Count == 0 && TotalDemand > 0)
                        OnNext(i);
                    else
                    {
                        _buffer = _buffer.Add(i);
                        DeliverBuffer();
                    }
                })
                .With<Request>(DeliverBuffer)
                .With<Cancel>(() => Context.Stop(Self))
                .WasHandled;
        }

        private void DeliverBuffer()
        {
            if (TotalDemand <= 0)
                return;

            if (TotalDemand <= int.MaxValue)
            {
                var use = _buffer.Take((int) TotalDemand).ToImmutableList();
                _buffer = _buffer.Skip((int) TotalDemand).ToImmutableList();

                use.ForEach(OnNext);
            }
            else
            {
                var use = _buffer.Take(int.MaxValue).ToImmutableList();
                _buffer = _buffer.Skip(int.MaxValue).ToImmutableList();

                use.ForEach(OnNext);
                DeliverBuffer();
            }
        }
    }

    internal class TimeoutingPublisher : ActorPublisher<int>
    {
        public static Props Props(IActorRef probe, TimeSpan timeout) =>
                Akka.Actor.Props.Create(() => new TimeoutingPublisher(probe, timeout))
                    .WithDispatcher("akka.test.stream-dispatcher");

        private readonly IActorRef _probe;

        public TimeoutingPublisher(IActorRef probe, TimeSpan timeout) 
        {
            _probe = probe;
            SubscriptionTimeout = timeout;
        }
        
        protected override bool Receive(object message)
        {
            return message.Match()
                .With<Request>(() => OnNext(1))
                .With<SubscriptionTimeoutExceeded>(() =>
                {
                    _probe.Tell("timed-out");
                    Context.System.Scheduler.ScheduleTellOnce(SubscriptionTimeout, _probe, "cleaned-up", Self);
                    Context.System.Scheduler.ScheduleTellOnce(SubscriptionTimeout, Self, PoisonPill.Instance, Nobody.Instance);
                })
                .WasHandled;
        }
    }

    internal class Receiver : ActorSubscriber
    {
        public static Props Props(IActorRef probe) =>
            Akka.Actor.Props.Create(() => new Receiver(probe)).WithDispatcher("akka.test.stream-dispatcher");

        private readonly IActorRef _probe;

        public Receiver(IActorRef probe)
        {
            _probe = probe;
        }

        public override IRequestStrategy RequestStrategy { get; } = new WatermarkRequestStrategy(10);

        protected override bool Receive(object message)
        {
            return message.Match()
                .With<OnNext>(next => _probe.Tell(next.Element))
                .WasHandled;
        }
    }

    internal class TotalDemand
    {
        public readonly long Elements;

        public TotalDemand(long elements)
        {
            Elements = elements;
        }
    }

    internal class Produce
    {
        public readonly string Elem;

        public Produce(string elem)
        {
            Elem = elem;
        }
    }

    internal class Err
    {
        public readonly string Reason;

        public Err(string reason)
        {
            Reason = reason;
        }
    }

    internal class ErrThenStop
    {
        public readonly string Reason;

        public ErrThenStop(string reason)
        {
            Reason = reason;
        }
    }

    internal class Boom
    {
        public static Boom Instance { get; } = new Boom();

        private Boom() { }
    }

    internal class Complete
    {
        public static Complete Instance { get; } = new Complete();

        private Complete() { }
    }

    internal class CompleteThenStop
    {
        public static CompleteThenStop Instance { get; } = new CompleteThenStop();

        private CompleteThenStop() { }
    }

    internal class ThreadName
    {
        public static ThreadName Instance { get; } = new ThreadName();

        private ThreadName() { }
    }
}
