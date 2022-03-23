/******************************************************************************
 * Copyright (C) Leap Motion, Inc. 2011-2018.                                 *
 * Leap Motion proprietary and confidential.                                  *
 *                                                                            *
 * Use subject to the terms of the Leap Motion SDK Agreement available at     *
 * https://developer.leapmotion.com/sdk_agreement, or another agreement       *
 * between Leap Motion and you, your company or other organization.           *
 ******************************************************************************/

using System;
using System.Collections.Generic;
using UnityEngine;
using Leap.Unity.Encoding;

namespace Leap.Unity
{
    using Attributes;

    /// <summary>
    /// Base class for aggregating frame data. Waits for frame data from all specified providers and then calls MergeFrames to combine them.
    /// Implement MergeFrames(Frame[] frames) in an inherited class
    /// </summary>
    public abstract class LeapAggregatedProviderBase : LeapProvider
    {
        #region Inspector

        /// <summary>
        /// A list of providers that are used for aggregation
        /// </summary>
        [Tooltip("Add all providers here that you want to be used for aggregation")]
        [EditTimeOnly]
        public LeapProvider[] providers;

        public enum FrameOptimizationMode
        {
            None,
            ReuseUpdateForPhysics,
            ReusePhysicsForUpdate,
        }
        [Tooltip("When enabled, the provider will only calculate one leap frame instead of two.")]
        [SerializeField]
        protected FrameOptimizationMode _frameOptimization = FrameOptimizationMode.None;


#if UNITY_2017_3_OR_NEWER
        [Tooltip("When checked, profiling data from the LeapCSharp worker thread will be used to populate the UnityProfiler.")]
        [EditTimeOnly]
#else
    [Tooltip("Worker thread profiling requires a Unity version of 2017.3 or greater.")]
    [Disable]
#endif
        [SerializeField]
        protected bool _workerThreadProfiling = false;

        #endregion

        #region Internal Settings & Memory

        protected Frame _transformedUpdateFrame, _transformedFixedFrame;

        // list of frames that are send to MergeFrames() to aggregate to a single frame
        Frame[] updateFramesToCombine;
        Frame[] fixedUpdateFramesToCombine;

        #endregion

        #region Edit-time Frame Data

#if UNITY_EDITOR
        private Frame _backingUntransformedEditTimeFrame = null;
        private Frame _untransformedEditTimeFrame
        {
            get
            {
                if (_backingUntransformedEditTimeFrame == null)
                {
                    _backingUntransformedEditTimeFrame = new Frame();
                }
                return _backingUntransformedEditTimeFrame;
            }
        }
        private Frame _backingEditTimeFrame = null;
        private Frame _editTimeFrame
        {
            get
            {
                if (_backingEditTimeFrame == null)
                {
                    _backingEditTimeFrame = new Frame();
                }
                return _backingEditTimeFrame;
            }
        }

        private Dictionary<TestHandFactory.TestHandPose, Hand> _cachedLeftHands
          = new Dictionary<TestHandFactory.TestHandPose, Hand>();
        private Hand _editTimeLeftHand
        {
            get
            {
                Hand cachedHand;
                if (_cachedLeftHands.TryGetValue(editTimePose, out cachedHand))
                {
                    return cachedHand;
                }
                else
                {
                    cachedHand = TestHandFactory.MakeTestHand(isLeft: true, pose: editTimePose);
                    _cachedLeftHands[editTimePose] = cachedHand;
                    return cachedHand;
                }
            }
        }

        private Dictionary<TestHandFactory.TestHandPose, Hand> _cachedRightHands
          = new Dictionary<TestHandFactory.TestHandPose, Hand>();
        private Hand _editTimeRightHand
        {
            get
            {
                Hand cachedHand;
                if (_cachedRightHands.TryGetValue(editTimePose, out cachedHand))
                {
                    return cachedHand;
                }
                else
                {
                    cachedHand = TestHandFactory.MakeTestHand(isLeft: false, pose: editTimePose);
                    _cachedRightHands[editTimePose] = cachedHand;
                    return cachedHand;
                }
            }
        }

#endif

        #endregion

        #region LeapProvider Implementation

        public override Frame CurrentFrame
        {
            get
            {
#if UNITY_EDITOR
                if (!Application.isPlaying)
                {
                    _editTimeFrame.Hands.Clear();
                    _untransformedEditTimeFrame.Hands.Clear();
                    _untransformedEditTimeFrame.Hands.Add(_editTimeLeftHand);
                    _untransformedEditTimeFrame.Hands.Add(_editTimeRightHand);
                    transformFrame(_untransformedEditTimeFrame, _editTimeFrame);
                    return _editTimeFrame;
                }
#endif
                if (_frameOptimization == FrameOptimizationMode.ReusePhysicsForUpdate)
                {
                    return _transformedFixedFrame;
                }
                else
                {
                    return _transformedUpdateFrame;
                }
            }
        }

        public override Frame CurrentFixedFrame
        {
            get
            {
#if UNITY_EDITOR
                if (!Application.isPlaying)
                {
                    _editTimeFrame.Hands.Clear();
                    _untransformedEditTimeFrame.Hands.Clear();
                    _untransformedEditTimeFrame.Hands.Add(_editTimeLeftHand);
                    _untransformedEditTimeFrame.Hands.Add(_editTimeRightHand);
                    transformFrame(_untransformedEditTimeFrame, _editTimeFrame);
                    return _editTimeFrame;
                }
#endif
                if (_frameOptimization == FrameOptimizationMode.ReuseUpdateForPhysics)
                {
                    return _transformedUpdateFrame;
                }
                else
                {
                    return _transformedFixedFrame;
                }
            }
        }

        #endregion

        #region Unity Events

        protected virtual void Reset()
        {
            editTimePose = TestHandFactory.TestHandPose.DesktopModeA;
        }

        protected virtual void OnValidate()
        {
            validateInput();
        }

        private void validateInput()
        {
            if (detectCycle(this, new List<LeapAggregatedProviderBase>()))
            {
                enabled = false;
                Debug.LogError("The input providers on the aggregation provider on " + gameObject.name
                             + " causes an infinite cycle, so it has been disabled.");
            }
        }

        /// <summary>
        /// aggregation providers wait for all their input providers' update events, so looping them won't work
        /// this detects a cycle
        /// </summary>
        private bool detectCycle(LeapAggregatedProviderBase currentProvider, List<LeapAggregatedProviderBase> seenProviders)
        {
            if (seenProviders.Contains(currentProvider)) return true;

            foreach(LeapProvider provider in currentProvider.providers)
            {
                if(provider is LeapAggregatedProviderBase)
                {
                    List<LeapAggregatedProviderBase> newSeenProvider = new List<LeapAggregatedProviderBase>(seenProviders);
                    newSeenProvider.Add(currentProvider);
                    if (detectCycle(provider as LeapAggregatedProviderBase, newSeenProvider)) return true;
                }
            }
            return false;
        }

        protected virtual void Awake()
        {
            // if any of the providers are aggregation providers, warn the user 
            foreach(LeapProvider provider in providers)
            {
                if(provider is LeapAggregatedProviderBase)
                {
                    Debug.LogWarning("You are trying to aggregate an aggregation provider. This might lead to latency. " +
                        "consider writing your own aggregator instead");
                }
            }

            updateFramesToCombine = new Frame[providers.Length];
            fixedUpdateFramesToCombine = new Frame[providers.Length];

            // subscribe to the update events and fixed update events of all providers in the public list 'providers'
            // when an update event happens, add its frame to the framesToCombine lists and then check whether the whole list is filled.
            // if it is, call updateFrame or updateFixedFrame
            for (int i = 0; i < providers.Length; i++)
            {
                int idx = i;
                providers[i].OnUpdateFrame += (x) =>
                {
                    updateFramesToCombine[idx] = x;
                    if(CheckFramesFilled(updateFramesToCombine)) UpdateFrame();
                };
                providers[i].OnFixedFrame += (x) =>
                {
                    fixedUpdateFramesToCombine[idx] = x;
                    if(CheckFramesFilled(fixedUpdateFramesToCombine)) UpdateFixedFrame();
                };
            }
        }

        protected virtual void Start()
        {
            _transformedUpdateFrame = new Frame();
            _transformedFixedFrame = new Frame();
        }

        #endregion

        #region aggregation functions

        bool CheckFramesFilled(Frame[] frames)
        {
            foreach(Frame frame in frames)
            {
                if (frame == null) return false; 
            }
            return true;
        }

        protected virtual void UpdateFrame()
        {
            // get timestamp and other frame info from the first leap provider
            _transformedUpdateFrame = MergeFrames(updateFramesToCombine);

            // reset all the update frames received from providers to null again
            for (int i = 0; i < updateFramesToCombine.Length; i++)
            {
                updateFramesToCombine[i] = null;
            }

            // ??? needed?
            if (_workerThreadProfiling)
            {
                LeapProfiling.Update();
            }

#if UNITY_EDITOR
            if (UnityEditor.EditorApplication.isCompiling)
            {
                UnityEditor.EditorApplication.isPlaying = false;
                Debug.LogWarning("Unity hot reloading not currently supported. Stopping Editor Playback.");
                return;
            }
#endif

            if (_frameOptimization == FrameOptimizationMode.ReusePhysicsForUpdate)
            {
                DispatchUpdateFrameEvent(_transformedFixedFrame);
                return;
            }

            if (_transformedUpdateFrame != null)
            {
                DispatchUpdateFrameEvent(_transformedUpdateFrame);
            }
        }

        protected virtual void UpdateFixedFrame()
        {

            // get timestamp and other frame info from the first leap provider
            _transformedFixedFrame = MergeFrames(fixedUpdateFramesToCombine);

            // reset all the update frames received from providers to null again
            for (int i = 0; i < fixedUpdateFramesToCombine.Length; i++)
            {
                fixedUpdateFramesToCombine[i] = null;
            }

            if (_frameOptimization == FrameOptimizationMode.ReuseUpdateForPhysics)
            {
                DispatchFixedFrameEvent(_transformedUpdateFrame);
                return;
            }

            if (_transformedFixedFrame != null)
            {
                DispatchFixedFrameEvent(_transformedFixedFrame);
            }
        }

        protected abstract Frame MergeFrames(Frame[] frames);

        #endregion

        #region Internal Methods

        protected virtual void transformFrame(Frame source, Frame dest)
        {
            dest.CopyFrom(source).Transform(transform.GetLeapMatrix());
        }

        #endregion

    }

}
