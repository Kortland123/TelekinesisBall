
using HarmonyLib;
using Il2Cpp;
using Il2CppInterop.Runtime.InteropTypes;
using MelonLoader;
using System;
using Unity.Netcode;
using UnityEngine;

namespace TelekinesisBall
{
    public static class TelekinesisFeature
    {
        private static BeetleActor _localActor;

        // Left-click ability.
        private static BeetleActor _targetBeetle;
        private static DungBall _targetBall;
        private static bool _grappling;
        private static bool _mouseHeld;

        // R-key ball pull.
        private static DungBall _rTargetBall;
        private static bool _rKeyHeld;

        // F-key vacuum.
        private static BeetleActor _vacuumBeetle;
        private static DungBall _vacuumBall;
        private static BeetleActor _vacuumHolder;

        private static bool _vacuumActive;
        private static bool _vacuumKeyHeld;
        private static bool _waitingForRelease;

        private static float _releaseTimeout;

        // Mouse sensitivity while actively force-holding a ball.
        private const float HeldBallSensitivityMultiplier = 0.30f;

        private static SettingSO_MouseSensitivity _mouseSensitivitySetting;
        private static float _originalMouseSensitivity;
        private static bool _hasOriginalMouseSensitivity;
        private static bool _sensitivityReduced;
        private static bool _changingSensitivityInternally;

        // Existing movement tuning.
        private const float PullSpeed = 15f;
        private const float BallPullForce = 3.75f;
        private const float StopDistance = 1.5f;

        // Existing vacuum tuning.
        private const float HoldDistance = 10f;
        private const float HoldHeight = 5f;
        private const float VacuumGain = 18f;
        private const float MaxVacuumSpeed = 72f;
        private const float ArrivalDistance = 0.2f;

        private const float ReleaseWaitTime = 1.5f;
        private const float RaycastDistance = 1000f;

        private static float _nextErrorLogTime;

        public static void Initialize(global::HarmonyLib.Harmony harmony)
        {
            harmony.Patch(
                AccessTools.Method(typeof(BeetleActor), "Update"),
                postfix: new HarmonyMethod(
                    typeof(TelekinesisFeature),
                    nameof(UpdatePostfix))
            );

            harmony.Patch(
                AccessTools.Method(typeof(BeetleActor), "FixedUpdate"),
                postfix: new HarmonyMethod(
                    typeof(TelekinesisFeature),
                    nameof(FixedUpdatePostfix))
            );

            harmony.Patch(
                AccessTools.PropertyGetter(typeof(DungBall), "CanBeGrabbed"),
                postfix: new HarmonyMethod(
                    typeof(TelekinesisFeature),
                    nameof(CanBeGrabbedPostfix))
            );

            harmony.Patch(
                AccessTools.Method(
                    typeof(SettingSO_MouseSensitivity),
                    "OnUpdateValue"),
                postfix: new HarmonyMethod(
                    typeof(TelekinesisFeature),
                    nameof(SensitivityUpdatedPostfix))
            );

            MelonLogger.Msg(
                "[TelekinesisBall] Universal telekinesis initialized.");
        }

        // Prevent the game's normal ball-grabbing ability.
        private static void CanBeGrabbedPostfix(ref bool __result)
        {
            __result = false;
        }

        private static bool IsLocalActor(BeetleActor actor)
        {
            if (actor == null)
                return false;

            NetworkActor networkActor = NetworkActor.LocalActor;

            if (networkActor == null)
                return false;

            BeetleActor localActor =
                ((Il2CppObjectBase)networkActor).TryCast<BeetleActor>();

            return localActor != null && actor == localActor;
        }

        private static void UpdatePostfix(BeetleActor __instance)
        {
            try
            {
                if (!IsLocalActor(__instance))
                    return;

                _localActor = __instance;

                UpdateVacuumInput(__instance);
                UpdatePullInput(__instance);
                UpdateMouseInput(__instance);

                UpdateMouseSensitivity();
            }
            catch (Exception ex)
            {
                LogError("Update error: " + ex);
            }
        }

        private static void FixedUpdatePostfix(BeetleActor __instance)
        {
            try
            {
                if (!IsLocalActor(__instance))
                    return;

                _localActor = __instance;

                UpdateVacuum();
                UpdateRPull();
                UpdateGrapple();
            }
            catch (Exception ex)
            {
                LogError("FixedUpdate error: " + ex);
            }
        }

        // ============================================================
        // MOUSE SENSITIVITY
        // ============================================================

        private static bool IsActivelyForceHoldingBall()
        {
            return _vacuumKeyHeld &&
                   _vacuumActive &&
                   !_waitingForRelease &&
                   _vacuumBall != null &&
                   _vacuumBall.RB != null;
        }

        private static void FindMouseSensitivitySetting()
        {
            if (_mouseSensitivitySetting != null)
                return;

            try
            {
                SettingSO_MouseSensitivity[] settings =
                    Resources.FindObjectsOfTypeAll<SettingSO_MouseSensitivity>();

                if (settings == null || settings.Length == 0)
                    return;

                foreach (SettingSO_MouseSensitivity setting in settings)
                {
                    if (setting == null)
                        continue;

                    _mouseSensitivitySetting = setting;

                    MelonLogger.Msg(
                        "[TelekinesisBall] Found mouse sensitivity setting. " +
                        "Current value: " + setting.GetValue());

                    return;
                }
            }
            catch (Exception ex)
            {
                LogError("Finding mouse sensitivity setting failed: " + ex);
            }
        }

        private static void UpdateMouseSensitivity()
        {
            FindMouseSensitivitySetting();

            if (_mouseSensitivitySetting == null)
                return;

            try
            {
                bool shouldReduce = IsActivelyForceHoldingBall();

                if (shouldReduce)
                {
                    if (!_hasOriginalMouseSensitivity)
                    {
                        _originalMouseSensitivity =
                            _mouseSensitivitySetting.GetValue();

                        _hasOriginalMouseSensitivity = true;
                    }

                    float reducedSensitivity =
                        _originalMouseSensitivity *
                        HeldBallSensitivityMultiplier;

                    float currentSensitivity =
                        _mouseSensitivitySetting.GetValue();

                    if (Math.Abs(currentSensitivity - reducedSensitivity) > 0.0001f)
                    {
                        SetMouseSensitivityInternally(reducedSensitivity);
                    }

                    if (!_sensitivityReduced)
                    {
                        _sensitivityReduced = true;

                        MelonLogger.Msg(
                            "[TelekinesisBall] Force-holding sensitivity reduced to 30%.");
                    }
                }
                else
                {
                    RestoreMouseSensitivity();
                }
            }
            catch (Exception ex)
            {
                LogError("Mouse sensitivity update failed: " + ex);
            }
        }

        private static void SetMouseSensitivityInternally(float value)
        {
            if (_mouseSensitivitySetting == null)
                return;

            try
            {
                _changingSensitivityInternally = true;
                _mouseSensitivitySetting.SetValue(value);
            }
            finally
            {
                _changingSensitivityInternally = false;
            }
        }

        private static void RestoreMouseSensitivity()
        {
            if (!_sensitivityReduced || !_hasOriginalMouseSensitivity)
                return;

            try
            {
                SetMouseSensitivityInternally(_originalMouseSensitivity);

                MelonLogger.Msg(
                    "[TelekinesisBall] Original mouse sensitivity restored.");
            }
            catch (Exception ex)
            {
                LogError("Mouse sensitivity restoration failed: " + ex);
            }
            finally
            {
                _sensitivityReduced = false;
                _hasOriginalMouseSensitivity = false;
            }
        }

        // If the player changes the sensitivity slider while holding a ball,
        // treat that new value as their preferred normal sensitivity.
        private static void SensitivityUpdatedPostfix(
            SettingSO_MouseSensitivity __instance,
            float value)
        {
            try
            {
                if (_changingSensitivityInternally)
                    return;

                if (__instance == null)
                    return;

                _mouseSensitivitySetting = __instance;

                if (_sensitivityReduced)
                {
                    _originalMouseSensitivity = value;
                    _hasOriginalMouseSensitivity = true;

                    if (IsActivelyForceHoldingBall())
                    {
                        float reducedValue =
                            value * HeldBallSensitivityMultiplier;

                        SetMouseSensitivityInternally(reducedValue);
                    }
                    else
                    {
                        _sensitivityReduced = false;
                        _hasOriginalMouseSensitivity = false;
                    }
                }
            }
            catch (Exception ex)
            {
                LogError("Sensitivity setting callback failed: " + ex);
            }
        }

        // ============================================================
        // INPUT
        // ============================================================

        private static void UpdateVacuumInput(BeetleActor actor)
        {
            bool held = Input.GetKey(KeyCode.F);

            if (held && !_vacuumKeyHeld)
                StartVacuum(actor);

            if (!held && _vacuumKeyHeld)
                StopVacuum();

            _vacuumKeyHeld = held;
        }

        private static void UpdatePullInput(BeetleActor actor)
        {
            bool held = Input.GetKey(KeyCode.R);

            if (held && !_rKeyHeld)
                StartRPull(actor);

            if (!held && _rKeyHeld)
                StopRPull();

            _rKeyHeld = held;
        }

        private static void UpdateMouseInput(BeetleActor actor)
        {
            bool held = Input.GetMouseButton(0);

            if (!held)
            {
                _mouseHeld = false;

                if (_grappling)
                    StopGrapple();

                return;
            }

            _mouseHeld = true;

            if (actor.HasBall)
            {
                if (_grappling)
                    StopGrapple();

                return;
            }

            if (!_grappling)
                TryStartGrapple(actor);
        }

        // ============================================================
        // TARGET SEARCH
        // ============================================================

        private static bool TryGetRaycastHit(out RaycastHit bestHit)
        {
            bestHit = default;

            Camera camera = Camera.main;

            if (camera == null)
                return false;

            Ray ray = new Ray(
                camera.transform.position,
                camera.transform.forward);

            RaycastHit[] hits = Physics.RaycastAll(
                ray,
                RaycastDistance,
                ~0,
                QueryTriggerInteraction.Ignore);

            if (hits == null || hits.Length == 0)
                return false;

            Array.Sort(
                hits,
                (a, b) => a.distance.CompareTo(b.distance));

            foreach (RaycastHit hit in hits)
            {
                if (hit.collider == null)
                    continue;

                BeetleActor beetle =
                    hit.collider.GetComponentInParent<BeetleActor>();

                if (beetle != null && beetle != _localActor)
                {
                    bestHit = hit;
                    return true;
                }

                DungBall ball =
                    hit.collider.GetComponentInParent<DungBall>();

                if (ball != null && ball.RB != null)
                {
                    bestHit = hit;
                    return true;
                }
            }

            return false;
        }

        private static bool TryGetTargets(
            out DungBall ball,
            out BeetleActor beetle)
        {
            ball = null;
            beetle = null;

            if (!TryGetRaycastHit(out RaycastHit hit))
                return false;

            if (hit.collider == null)
                return false;

            ball = hit.collider.GetComponentInParent<DungBall>();

            if (ball != null && ball.RB != null)
                return true;

            beetle = hit.collider.GetComponentInParent<BeetleActor>();

            if (beetle != null && beetle != _localActor)
                return true;

            return false;
        }

        // ============================================================
        // LEFT CLICK: PUSH BALL / PULL BEETLE
        // ============================================================

        private static void TryStartGrapple(BeetleActor actor)
        {
            if (!TryGetTargets(out DungBall ball, out BeetleActor beetle))
                return;

            _targetBall = ball;
            _targetBeetle = beetle;
            _grappling = true;

            if (_targetBall != null)
            {
                MelonLogger.Msg(
                    "[TelekinesisBall] Left-click pushing ball.");
            }
            else if (_targetBeetle != null)
            {
                MelonLogger.Msg(
                    "[TelekinesisBall] Left-click pulling beetle.");
            }
        }

        private static void UpdateGrapple()
        {
            if (!_grappling || !_mouseHeld || _localActor == null)
                return;

            if (_targetBall != null)
            {
                PushBall(_targetBall);
                return;
            }

            if (_targetBeetle != null)
            {
                PullPlayer(_targetBeetle);
            }
        }

        private static void PushBall(DungBall ball)
        {
            if (ball == null || ball.RB == null || _localActor == null)
                return;

            Vector3 difference =
                ball.transform.position - _localActor.transform.position;

            if (difference.sqrMagnitude < 0.001f)
                return;

            Vector3 force = difference.normalized * BallPullForce;

            RigidbodyForceRpcDispatcher.Params parameters =
                new RigidbodyForceRpcDispatcher.Params(
                    force,
                    false,
                    true,
                    true,
                    false);

            ball.ForceDispatcher.DispatchForceRpcOrPerformLocal(
                parameters);
        }

        private static void PullPlayer(BeetleActor beetle)
        {
            if (beetle == null || _localActor == null)
                return;

            Vector3 difference =
                _localActor.transform.position - beetle.transform.position;

            if (difference.magnitude <= StopDistance)
            {
                beetle.SetLinearVelocity(Vector3.zero);
                return;
            }

            beetle.SetLinearVelocity(
                difference.normalized * PullSpeed);
        }

        private static void StopGrapple()
        {
            _grappling = false;
            _targetBall = null;
            _targetBeetle = null;

            MelonLogger.Msg(
                "[TelekinesisBall] Left-click ability stopped.");
        }

        // ============================================================
        // R KEY: PULL BALL TOWARD LOCAL BEETLE
        // ============================================================

        private static void StartRPull(BeetleActor actor)
        {
            if (actor == null || actor.HasBall)
                return;

            if (!TryGetTargets(out DungBall ball, out BeetleActor beetle))
                return;

            if (ball == null || ball.RB == null)
                return;

            _rTargetBall = ball;

            MelonLogger.Msg(
                "[TelekinesisBall] R-key ball pull started.");
        }

        private static void UpdateRPull()
        {
            if (!_rKeyHeld || _rTargetBall == null || _localActor == null)
                return;

            if (_rTargetBall.RB == null)
                return;

            Vector3 difference =
                _localActor.transform.position -
                _rTargetBall.transform.position;

            if (difference.sqrMagnitude < 0.001f)
                return;

            Vector3 force = difference.normalized * BallPullForce;

            RigidbodyForceRpcDispatcher.Params parameters =
                new RigidbodyForceRpcDispatcher.Params(
                    force,
                    false,
                    true,
                    true,
                    false);

            _rTargetBall.ForceDispatcher.DispatchForceRpcOrPerformLocal(
                parameters);
        }

        private static void StopRPull()
        {
            _rTargetBall = null;

            MelonLogger.Msg(
                "[TelekinesisBall] R-key ball pull stopped.");
        }

        // ============================================================
        // F KEY: VACUUM / BALL STEALING
        // ============================================================

        private static void StartVacuum(BeetleActor actor)
        {
            if (actor == null)
                return;

            if (!TryGetTargets(out DungBall ball, out BeetleActor beetle))
                return;

            _vacuumBall = ball;
            _vacuumBeetle = beetle;
            _vacuumHolder = null;

            _vacuumActive = false;
            _waitingForRelease = false;

            if (_vacuumBall == null && _vacuumBeetle != null)
            {
                _vacuumActive = true;

                MelonLogger.Msg(
                    "[TelekinesisBall] F-key beetle pull started.");

                return;
            }

            if (_vacuumBall == null || _vacuumBall.RB == null)
                return;

            _vacuumHolder = FindBallHolder(_vacuumBall);

            if (_vacuumHolder != null)
            {
                RequestHolderRelease(_vacuumHolder);

                _waitingForRelease = true;
                _releaseTimeout = Time.time + ReleaseWaitTime;

                MelonLogger.Msg(
                    "[TelekinesisBall] Requested networked release " +
                    "from previous ball holder.");

                return;
            }

            _vacuumActive = true;

            MelonLogger.Msg(
                "[TelekinesisBall] F-key vacuum started.");
        }

        private static BeetleActor FindBallHolder(DungBall ball)
        {
            if (ball == null)
                return null;

            try
            {
                BeetleActor[] actors =
                    UnityEngine.Object.FindObjectsOfType<BeetleActor>();

                foreach (BeetleActor actor in actors)
                {
                    if (actor == null)
                        continue;

                    DungBall heldBall;

                    if (actor.TryGetHeldBall(out heldBall) &&
                        heldBall != null &&
                        heldBall == ball)
                    {
                        return actor;
                    }
                }
            }
            catch (Exception ex)
            {
                LogError("FindBallHolder error: " + ex);
            }

            return null;
        }

        private static void RequestHolderRelease(BeetleActor holder)
        {
            if (holder == null)
                return;

            try
            {
                BeetleActorReleaseBallRpcDispatcher dispatcher =
                    holder.ReleaseBallRpcDispatcher;

                if (dispatcher == null)
                {
                    MelonLogger.Warning(
                        "[TelekinesisBall] Holder has no release dispatcher.");
                    return;
                }

                ulong responsibleClientId = 0;

                if (NetworkManager.Singleton != null)
                {
                    responsibleClientId =
                        NetworkManager.Singleton.LocalClientId;
                }

                dispatcher.DispatchRpcOrPerformLocal(
                    responsibleClientId);
            }
            catch (Exception ex)
            {
                LogError("Holder release request error: " + ex);
            }
        }

        private static void UpdateVacuum()
        {
            if (!_vacuumKeyHeld || _localActor == null)
                return;

            if (_waitingForRelease)
            {
                CheckHolderRelease();
                return;
            }

            if (!_vacuumActive)
                return;

            if (_vacuumBall != null)
            {
                HoldBallInFront(_vacuumBall);
                return;
            }

            if (_vacuumBeetle != null)
            {
                PullPlayer(_vacuumBeetle);
            }
        }

        private static void CheckHolderRelease()
        {
            if (_vacuumBall == null)
            {
                _waitingForRelease = false;
                return;
            }

            bool stillHeld = false;

            if (_vacuumHolder != null)
            {
                DungBall heldBall;

                stillHeld =
                    _vacuumHolder.TryGetHeldBall(out heldBall) &&
                    heldBall != null &&
                    heldBall == _vacuumBall;
            }

            if (!stillHeld)
            {
                _waitingForRelease = false;
                _vacuumActive = true;
                _vacuumHolder = null;

                MelonLogger.Msg(
                    "[TelekinesisBall] Ball released; vacuum control active.");

                return;
            }

            if (Time.time >= _releaseTimeout)
            {
                _waitingForRelease = false;
                _vacuumActive = false;
                _vacuumHolder = null;

                MelonLogger.Warning(
                    "[TelekinesisBall] Ball release was not confirmed. " +
                    "Vacuum was not started.");
            }
        }

        private static Vector3 GetVacuumDestination()
        {
            Camera camera = Camera.main;

            if (camera == null)
            {
                return _localActor.transform.position +
                       _localActor.transform.forward * HoldDistance +
                       Vector3.up * HoldHeight;
            }

            return _localActor.transform.position +
                   camera.transform.forward * HoldDistance +
                   Vector3.up * HoldHeight;
        }

        private static void HoldBallInFront(DungBall ball)
        {
            if (ball == null || ball.RB == null)
                return;

            Vector3 destination = GetVacuumDestination();

            Vector3 difference =
                destination - ball.RB.position;

            if (difference.magnitude <= ArrivalDistance)
            {
                ball.RB.velocity = Vector3.zero;
                return;
            }

            Vector3 velocity =
                difference * VacuumGain;

            ball.RB.velocity =
                Vector3.ClampMagnitude(velocity, MaxVacuumSpeed);
        }

        private static void StopVacuum()
        {
            _vacuumActive = false;
            _waitingForRelease = false;

            _vacuumBall = null;
            _vacuumBeetle = null;
            _vacuumHolder = null;

            MelonLogger.Msg(
                "[TelekinesisBall] F-key vacuum stopped.");
        }

        // ============================================================
        // LOGGING
        // ============================================================

        private static void LogError(string message)
        {
            if (Time.time < _nextErrorLogTime)
                return;

            _nextErrorLogTime = Time.time + 2f;

            MelonLogger.Error(
                "[TelekinesisBall] " + message);
        }
    }
}
