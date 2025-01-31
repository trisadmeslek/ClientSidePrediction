using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using Mirage.Logging;
using UnityEngine;
using UnityEngine.Assertions;

namespace JamesFrowen.CSP
{
    public class TickRunner : IPredictionTime
    {
        public delegate void OnTick(int tick);
        static readonly ILogger logger = LogFactory.GetLogger<TickRunner>();

        public float TickRate = 50;

        /// <summary>
        /// Max milliseconds per frame to process. Wont start new Ticks if current frame is over this limit.
        /// <para>
        /// This can avoid freezes if ticks start to take a long time.
        /// </para>
        /// <para>
        /// The runner will try to run <see cref="TickRate"/> per second, but if they take longer than 1 second then each frame will get longer and longer.
        /// This limit will stops extra ticks in that frame from being processed, allowing other parts of the applications (eg message processing).
        /// <para>
        /// Any stopped ticks will run next frame instead
        /// </para>
        /// </para>
        /// </summary>
        public float MaxFrameTime = 200;

        protected int _tick;

        /// <summary>
        /// Used by client to keep up with server
        /// <para>always 1 on server</para>
        /// </summary>
        public float TimeScale { get; protected set; } = 1;

        readonly Stopwatch stopwatch;
        double tickTimer;
        double lastFrame;
        /// <summary>
        /// keep track of last tick invoked on event, incase client jumps to line up with server
        /// </summary>
        int lastInvokedTick;


        /// <summary>
        /// Called once a frame, before any ticks
        /// </summary>
        public event Action onEarlyUpdate;

        /// <summary>
        /// Make tick update event, Called before <see cref="onTick"/>
        /// </summary>
        public event OnTick onPreTick;
        /// <summary>
        /// Make tick update event
        /// </summary>
        public event OnTick onTick;
        /// <summary>
        /// Late tick update event, Called after <see cref="onTick"/>
        /// </summary>
        public event OnTick onPostTick;

        public TickRunner()
        {
            stopwatch = Stopwatch.StartNew();
        }

        public float FixedDeltaTime
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => 1f / TickRate;
        }

        public int Tick
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _tick;
        }

        public double UnscaledTime
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => stopwatch.Elapsed.TotalSeconds;
        }

        bool IPredictionTime.IsResimulation => false;
        float IPredictionTime.FixedTime => Tick * FixedDeltaTime;

        double GetCurrentTime()
        {
            return stopwatch.Elapsed.TotalSeconds;
        }

        public virtual void OnUpdate()
        {
            double now = GetCurrentTime();
            int startTick = _tick;
            double max = now + (MaxFrameTime / 1000f);
            double delta = now - lastFrame;
            lastFrame = now;

            onEarlyUpdate?.Invoke();
            
            tickTimer += delta * TimeScale;
            while (tickTimer > FixedDeltaTime)
            {
                tickTimer -= FixedDeltaTime;
                _tick++;

                // only invoke is tick is later, see lastInvokedTick
                // todo what if we jump back, do we not need to resimulate?
                if (_tick > lastInvokedTick)
                {
                    onPreTick?.Invoke(_tick);
                    onTick?.Invoke(_tick);
                    onPostTick?.Invoke(_tick);
                    lastInvokedTick = _tick;
                }

                if (GetCurrentTime() > max)
                {
                    if (logger.WarnEnabled()) logger.LogWarning($"Took longer than {MaxFrameTime}ms to process frame. Processed {_tick - startTick} ticks in {(GetCurrentTime() - now) * 1000f}ms");
                    break;
                }
            }
        }

        // have this virtual methods here, just so we have use 1 field for TickRunner.
        // we will only call this method on client so it should be a ClientTickRunner
        public virtual void OnMessage(int serverTick, double clientTime) => throw new NotSupportedException("OnMessage is not supported for default tick runner. See ClientTickRunner");
    }

    public class ClientTickRunner : TickRunner
    {
        static readonly ILogger logger = LogFactory.GetLogger<ClientTickRunner>();

        // this number neeeds to be less than buffer size in order for resimulation to work correctly
        // this number will clamp RTT average to a max value, so it should recover faster after RTT is back to normal
        const float MAX_RTT = 1.0f;

        readonly SimpleMovingAverage _RTTAverage;

        readonly float fastScale = 1.01f;
        readonly float normalScale = 1f;
        readonly float slowScale = 0.99f;

        readonly float positiveThreshold;
        readonly float negativeThreshold;
        readonly float skipAheadThreshold;

        bool intialized;
        int latestServerTick;

        //public float ClientDelaySeconds => ClientDelay * FixedDeltaTime;

#if DEBUG
        public float Debug_DelayInTicks { get; private set; }
        public SimpleMovingAverage Debug_RTT => _RTTAverage;
#endif

        /// <summary>
        /// Invoked at start AND if client gets too get away from server
        /// </summary>
        public event Action OnTickSkip;

        /// <param name="diffThreshold">how many ticks off the client time can be before changing speed, In ticks</param>
        /// <param name="timeScaleModifier">how much to speed up/slow down by is behind/ahead</param>
        /// <param name="skipThreshold">skip ahead to server tick if this far behind</param>
        /// <param name="movingAverageCount">how many ticks used in average, increase or decrease with framerate</param>
        public ClientTickRunner(float diffThreshold = 1.5f, float timeScaleModifier = 0.01f, float skipThreshold = 10f, int movingAverageCount = 100)
        {
            // IMPORTANT: most of these values are in tick NOT seconds, so careful when using them

            // if client is off by 0.5 then speed up/slow down
            positiveThreshold = diffThreshold;
            negativeThreshold = -positiveThreshold;

            // skip ahead if client fall behind by this many ticks
            skipAheadThreshold = skipThreshold;

            // speed up/slow down up by 0.01 if after/behind
            // we never want to be behind so catch up faster
            fastScale = normalScale + (timeScaleModifier * 5);
            slowScale = normalScale - timeScaleModifier;

            _RTTAverage = new SimpleMovingAverage(movingAverageCount);
        }

        public void ResetTime()
        {
            _RTTAverage.Reset();
            intialized = false;
        }

        public override void OnUpdate()
        {
            // only update client tick if server has sent first state
            if (intialized)
                base.OnUpdate();
        }

        bool CheckOrder(int serverTick)
        {
            if (serverTick <= latestServerTick)
            {
                logger.LogError($"Received message out of order server:{latestServerTick}, new:{serverTick}");
                return false;
            }
            latestServerTick = serverTick;
            return true;
        }

        /// <summary>
        /// Updates <see cref="clientScaleTime"/> to keep <see cref="ClientTime"/> in line with <see cref="LatestServerTime"/>
        /// </summary>
        /// <param name="serverTime"></param>
        public override void OnMessage(int serverTick, double clientSendTime)
        {
            if (!CheckOrder(serverTick))
                return;

            AddTimeToAverage(clientSendTime);
#if DEBUG
            VerboseLog(serverTick, clientSendTime);
#endif

            // if first message set client time to server-diff
            // reset stuff if too far behind
            // todo check this is correct
            if (!intialized)
            {
#if DEBUG
                Debug("serverTick,serverGuess,localTick,delayInTicks,delayInSeconds,delayFromLag,delayFromJitter,diff,newRTT,");
#endif
                InitNew(serverTick);
                return;
            }

            // guess what tick we have to be to reach serever in time
            float serverGuess = _tick - DelayInTicks();
            // how far was out guess off?
            float diff = serverTick - serverGuess;

            // if diff is bad enough, skip ahead
            // todo do we need abs, do also want to skip back if we are very ahead?
            // todo will skipping behind cause negative effects? we dont want Tick event to be invoked for a tick twice
            if (Math.Abs(diff) > skipAheadThreshold)
            {
                logger.LogWarning($"Client fell behind, skipping ahead. server:{serverTick:0.00} serverGuess:{serverGuess} diff:{diff:0.00}");
                InitNew(serverTick);
                return;
            }

            // apply timescale to try get closer to server
            AdjustClientTimeScale(diff);

            //todo add trace level
            if (logger.LogEnabled()) logger.Log($"st {serverTick:0.00} sg {serverGuess:0.00} ct {_tick:0.00} diff {diff * 1000:0.0}, wanted:{diff * 1000:0.0}, scale:{TimeScale}");
        }

        private float DelayInTicks()
        {
            (float lag, float jitter) = _RTTAverage.GetAverageAndStandardDeviation();

            // *2 so we have 2 stdDev worth of range
            float delayFromJitter = jitter * 2;
            float delayFromLag = lag;
            float delayInSeconds = delayFromLag + delayFromJitter;
            // +1 tick to make sure we are always ahead
            float delayInTicks = (delayInSeconds * TickRate) + 1;
#if DEBUG
            Debug_DelayInTicks = delayInTicks;
#endif
            return delayInTicks;
        }

        private void AddTimeToAverage(double clientSendTime)
        {
            // only add if client time was returned from server
            // it will be zero before client sends first input
            if (clientSendTime != 0)
            {
                double newRTT = UnscaledTime - clientSendTime;
                if (newRTT > MAX_RTT)
                {
                    if (logger.WarnEnabled())
                        logger.LogWarning($"return trip time is over max of {MAX_RTT}s, value:{newRTT * 1000:0.0}ms");
                    newRTT = MAX_RTT;
                }
                Assert.IsTrue(newRTT > 0);
                _RTTAverage.Add((float)newRTT);
            }
            else
            {
                // just add 150 ms as tick RTT
                _RTTAverage.Add(0.150f);
            }
        }

        private void InitNew(int serverTick)
        {
            _tick = Mathf.CeilToInt(serverTick + DelayInTicks());
            TimeScale = normalScale;
            intialized = true;
            // todo do we need to invoke this at start as well as skip?
            OnTickSkip?.Invoke();
        }

        private void AdjustClientTimeScale(float diff)
        {
            // diff is server-client,
            // if positive then server is ahead, => we can run client faster to catch up
            // if negative then server is behind, => we need to run client slow to not run out of spanshots

            // we want diffVsGoal to be as close to 0 as possible

            // server ahead, speed up client
            if (diff > positiveThreshold)
                TimeScale = fastScale;
            // server behind, slow down client
            else if (diff < negativeThreshold)
                TimeScale = slowScale;
            // close enough
            else
                TimeScale = normalScale;
        }


#if DEBUG
        private void VerboseLog(int serverTick, double clientSendTime)
        {
            (float lag, float jitter) = _RTTAverage.GetAverageAndStandardDeviation();
            float delayFromJitter = jitter * 2;
            float delayFromLag = lag;
            float delayInSeconds = delayFromLag + delayFromJitter;

            float serverGuess = _tick - DelayInTicks();
            float diff = serverTick - serverGuess;

            double newRTT = UnscaledTime - clientSendTime;
            Debug($"{serverTick},{serverGuess},{_tick},{(float)DelayInTicks()},{delayInSeconds},{delayFromLag},{delayFromJitter},{diff},{newRTT},");
        }

        static StreamWriter _writer = new StreamWriter(Path.Combine(Application.persistentDataPath, "ClientTickRunner.log")) { AutoFlush = true };
        void Debug(string line)
        {
            _writer.WriteLine(line);
        }
#endif
    }
}
