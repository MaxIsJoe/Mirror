using System;
using System.Collections.Generic;
using UnityEngine;

namespace Mirror.Examples.SnapshotInterpolationDemo
{
    public class ClientCube : MonoBehaviour
    {
        [Header("Components")]
        public ServerCube server;
        public Renderer render;

        [Header("Toggle")]
        public bool interpolate = true;

        // snapshot interpolation settings
        [Header("Snapshot Interpolation")]
        public SnapshotInterpolationSettings snapshotSettings =
            new SnapshotInterpolationSettings();

        // runtime settings
        public double bufferTime => server.sendInterval * snapshotSettings.bufferTimeMultiplier;

        // <servertime, snaps>
        public SortedList<double, Snapshot3D> snapshots = new SortedList<double, Snapshot3D>();

        // for smooth interpolation, we need to interpolate along server time.
        // any other time (arrival on client, client local time, etc.) is not
        // going to give smooth results.
        double localTimeline;

        // catchup / slowdown adjustments are applied to timescale,
        // to be adjusted in every update instead of when receiving messages.
        double localTimescale = 1;

        // we use EMA to average the last second worth of snapshot time diffs.
        // manually averaging the last second worth of values with a for loop
        // would be the same, but a moving average is faster because we only
        // ever add one value.
        ExponentialMovingAverage driftEma;
        ExponentialMovingAverage deliveryTimeEma; // average delivery time (standard deviation gives average jitter)

        // debugging ///////////////////////////////////////////////////////////
        [Header("Debug")]
        public Color catchupColor = Color.green; // green traffic light = go fast
        public Color slowdownColor = Color.red;  // red traffic light = go slow
        Color defaultColor;

        [Header("Simulation")]
        bool lowFpsMode;
        double accumulatedDeltaTime;

        void Awake()
        {
            defaultColor = render.sharedMaterial.color;

            // initialize EMA with 'emaDuration' seconds worth of history.
            // 1 second holds 'sendRate' worth of values.
            // multiplied by emaDuration gives n-seconds.
            driftEma = new ExponentialMovingAverage(server.sendRate * snapshotSettings.driftEmaDuration);
            deliveryTimeEma = new ExponentialMovingAverage(server.sendRate * snapshotSettings.deliveryTimeEmaDuration);
        }

        // add snapshot & initialize client interpolation time if needed
        public void OnMessage(Snapshot3D snap)
        {
            // set local timestamp (= when it was received on our end)
#if !UNITY_2020_3_OR_NEWER
            snap.localTime = NetworkTime.localTime;
#else
            snap.localTime = Time.timeAsDouble;
#endif

            // (optional) dynamic adjustment
            if (snapshotSettings.dynamicAdjustment)
            {
                // set bufferTime on the fly.
                // shows in inspector for easier debugging :)
                snapshotSettings.bufferTimeMultiplier = SnapshotInterpolation.DynamicAdjustment(
                    server.sendInterval,
                    deliveryTimeEma.StandardDeviation,
                    snapshotSettings.dynamicAdjustmentTolerance
                );
            }

            // insert into the buffer & initialize / adjust / catchup
            SnapshotInterpolation.InsertAndAdjust(
                snapshots,
                snap,
                ref localTimeline,
                ref localTimescale,
                server.sendInterval,
                bufferTime,
                snapshotSettings.catchupSpeed,
                snapshotSettings.slowdownSpeed,
                ref driftEma,
                snapshotSettings.catchupNegativeThreshold,
                snapshotSettings.catchupPositiveThreshold,
                ref deliveryTimeEma);
        }

        void Update()
        {
            // accumulated delta allows us to simulate correct low fps + deltaTime
            // if necessary in client low fps mode.
            accumulatedDeltaTime += Time.unscaledDeltaTime;

            // simulate low fps mode. only update once per second.
            // to simulate webgl background tabs, etc.
            // after a while, disable low fps mode and see how it behaves.
            if (lowFpsMode && accumulatedDeltaTime < 1) return;

            // only while we have snapshots.
            // timeline starts when the first snapshot arrives.
            if (snapshots.Count > 0)
            {
                // snapshot interpolation
                if (interpolate)
                {
                    // step
                    SnapshotInterpolation.Step(
                        snapshots,
                        // accumulate delta is Time.unscaledDeltaTime normally.
                        // and sum of past 10 delta's in low fps mode.
                        accumulatedDeltaTime,
                        ref localTimeline,
                        localTimescale,
                        out Snapshot3D fromSnapshot,
                        out Snapshot3D toSnapshot,
                        out double t);

                    // interpolate & apply
                    Snapshot3D computed = Snapshot3D.Interpolate(fromSnapshot, toSnapshot, t);
                    transform.position = computed.position;
                }
                // apply raw
                else
                {
                    Snapshot3D snap = snapshots.Values[0];
                    transform.position = snap.position;
                    snapshots.RemoveAt(0);
                }
            }

            // reset simulation helpers
            accumulatedDeltaTime = 0;

            // color material while catching up / slowing down
            if (localTimescale < 1)
                render.material.color = slowdownColor;
            else if (localTimescale > 1)
                render.material.color = catchupColor;
            else
                render.material.color = defaultColor;
        }

        void OnGUI()
        {
            // display buffer size as number for easier debugging.
            // catchup is displayed as color state in Update() already.
            const int width = 30; // fit 3 digits
            const int height = 20;
            Vector2 screen = Camera.main.WorldToScreenPoint(transform.position);
            string str = $"{snapshots.Count}";
            GUI.Label(new Rect(screen.x - width / 2, screen.y - height / 2, width, height), str);

            // client simulation buttons on the bottom of the screen
            float areaHeight = 100;
            GUILayout.BeginArea(new Rect(0, Screen.height - areaHeight, Screen.width, areaHeight));
            GUILayout.BeginHorizontal();
            GUILayout.Label("Client Simulation:");
            if (GUILayout.Button((lowFpsMode ? "Disable" : "Enable") + " 1 FPS"))
            {
                lowFpsMode = !lowFpsMode;
            }
            if (GUILayout.Button("Timeline 100ms behind"))
            {
                localTimeline -= 0.1;
            }
            if (GUILayout.Button("Timeline 100ms ahead"))
            {
                localTimeline += 0.1;
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.EndArea();
        }

        void OnValidate()
        {
            // thresholds need to be <0 and >0
            snapshotSettings.catchupNegativeThreshold = Math.Min(snapshotSettings.catchupNegativeThreshold, 0);
            snapshotSettings.catchupPositiveThreshold = Math.Max(snapshotSettings.catchupPositiveThreshold, 0);
        }
    }
}
