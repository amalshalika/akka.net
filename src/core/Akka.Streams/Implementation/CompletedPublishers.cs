﻿using System;
using System.Reactive.Streams;
using System.Threading.Tasks;
using Akka.Streams.Util;

namespace Akka.Streams.Implementation
{
    internal sealed class EmptyPublisher<T> : IPublisher<T>
    {
        public static readonly IPublisher<T> Instance = new EmptyPublisher<T>();
        private EmptyPublisher() { }

        public void Subscribe(ISubscriber<T> subscriber)
        {
            try
            {
                ReactiveStreamsCompliance.RequireNonNullSubscriber(subscriber);
                ReactiveStreamsCompliance.TryOnSubscribe(subscriber, CancelledSubscription.Instance);
                ReactiveStreamsCompliance.TryOnComplete(subscriber);
            }
            catch (Exception e)
            {
                if (!(e is ISpecViolation))
                    throw;
            }
        }

        public override string ToString()
        {
            return "already-completed-publisher";
        }

        void IPublisher.Subscribe(ISubscriber subscriber)
        {
            Subscribe((ISubscriber<T>)subscriber);
        }
    }

    internal sealed class ErrorPublisher<T> : IPublisher<T>
    {
        public readonly string Name;
        public readonly Exception Cause;

        public ErrorPublisher(Exception cause, string name)
        {
            Name = name;
            Cause = cause;
        }

        public void Subscribe(ISubscriber<T> subscriber)
        {
            try
            {
                ReactiveStreamsCompliance.RequireNonNullSubscriber(subscriber);
                ReactiveStreamsCompliance.TryOnSubscribe(subscriber, CancelledSubscription.Instance);
                ReactiveStreamsCompliance.TryOnError(subscriber, Cause);
            }
            catch (Exception e)
            {
                if (!(e is ISpecViolation))
                    throw;
            }
        }

        public override string ToString()
        {
            return Name;
        }

        void IPublisher.Subscribe(ISubscriber subscriber)
        {
            Subscribe((ISubscriber<T>)subscriber);
        }
    }

    internal sealed class MaybePublisher<T> : IPublisher<T>
    {
        private class MaybeSubscription : ISubscription
        {
            private readonly ISubscriber<T> _subscriber;
            private readonly TaskCompletionSource<T> _promise;
            private bool _done = false;

            public MaybeSubscription(ISubscriber<T> subscriber, TaskCompletionSource<T> promise)
            {
                _subscriber = subscriber;
                _promise = promise;
            }

            public void Request(long n)
            {
                if (n < 1) ReactiveStreamsCompliance.RejectDueToNonPositiveDemand(_subscriber);
                if (!_done)
                {
                    _done = true;
                    _promise.Task.ContinueWith(t =>
                    {
                        if (!_promise.Task.Result.IsDefaultForType())
                        {
                            ReactiveStreamsCompliance.TryOnNext(_subscriber, _promise.Task.Result);
                            ReactiveStreamsCompliance.TryOnComplete(_subscriber);
                        }
                        else
                        {
                            ReactiveStreamsCompliance.TryOnComplete(_subscriber);
                        }
                    }, TaskContinuationOptions.OnlyOnRanToCompletion);
                }
            }

            public void Cancel()
            {
                _done = true;
                _promise.TrySetResult(default(T));
            }
        }

        public readonly TaskCompletionSource<T> Promise;
        public readonly string Name;

        public MaybePublisher(TaskCompletionSource<T> promise, string name)
        {
            Promise = promise;
            Name = name;
        }

        public void Subscribe(ISubscriber<T> subscriber)
        {
            try
            {
                ReactiveStreamsCompliance.RequireNonNullSubscriber(subscriber);
                ReactiveStreamsCompliance.TryOnSubscribe(subscriber, new MaybeSubscription(subscriber, Promise));
                Promise.Task.ContinueWith(t =>
                {
                    ReactiveStreamsCompliance.TryOnError(subscriber, t.Exception);
                }, TaskContinuationOptions.NotOnRanToCompletion);
            }
            catch (Exception)
            {
                //case sv: SpecViolation ⇒ ec.reportFailure(sv)
                throw;
            }
        }

        void IPublisher.Subscribe(ISubscriber subscriber)
        {
            Subscribe((ISubscriber<T>)subscriber);
        }

        public override string ToString()
        {
            return Name;
        }
    }

    internal sealed class CancelledSubscription : ISubscription
    {
        public static readonly CancelledSubscription Instance = new CancelledSubscription();
        private CancelledSubscription() { }

        public void Request(long n) { }

        public void Cancel() { }
    }

    internal sealed class CancellingSubscriber<T> : ISubscriber<T>
    {
        public void OnSubscribe(ISubscription subscription)
        {
            subscription.Cancel();
        }
        public void OnNext(T element) { }
        public void OnNext(object element) { }
        public void OnError(Exception cause) { }
        public void OnComplete() { }
    }

    internal sealed class RejectAdditionalSubscribers<T> : IPublisher<T>
    {
        public static readonly IPublisher<T> Instance = new RejectAdditionalSubscribers<T>();
        private RejectAdditionalSubscribers() { }

        public void Subscribe(ISubscriber<T> subscriber)
        {
            try
            {
                ReactiveStreamsCompliance.RejectAdditionalSubscriber(subscriber, "Publisher");
            }
            catch (Exception e)
            {
                if (!(e is ISpecViolation))
                    throw;
            }
        }

        void IPublisher.Subscribe(ISubscriber subscriber)
        {
            Subscribe((ISubscriber<T>)subscriber);
        }

        public override string ToString()
        {
            return "already-subscribed-publisher";
        }
    }
}