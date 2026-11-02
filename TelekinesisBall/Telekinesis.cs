using HarmonyLib;
using Il2Cpp;
using Il2CppInterop.Runtime.InteropTypes;
using MelonLoader;
using System;
using UnityEngine;

namespace TelekinesisBall
{
    public static class TelekinesisFeature
    {
        private static BeetleActor _localActor;

        // Left-click telekinesis.
        private static BeetleActor _targetBeetle;
        private static DungBall _targetBall;
        private static bool _grappling;
        private static bool _mouseHeld;

        // F-key vacuum.
        private static BeetleActor _vacuumBeetle;
        private static DungBall _vacuumBall;
        private static bool _vacuumActive;
        private static bool _vacuumKeyHeld;

        private const float PullSpeed = 15f;
        private const float BallPullForce = 3.75f;
        private const float StopDistance = 1.5f;

        // Vacuum positioning.
        private const float HoldDistance = 10f;
        private const float HoldHeight = 5f;
        private const float VacuumGain = 30f;
        private const float MaxVacuumSpeed = 120f;
        private const float ArrivalDistance = 0.2f;

        private static float _nextSearchLogTime;
        private static float _nextErrorLogTime;

        public static void Initialize(
            global::HarmonyLib.Harmony harmony)
        {
            harmony.Patch(
                AccessTools.Method(typeof(BeetleActor), "Update"),
                postfix: new HarmonyMethod(
                    typeof(TelekinesisFeature),
                    nameof(UpdatePostfix)
                )
            );

            harmony.Patch(
                AccessTools.Method(typeof(BeetleActor), "FixedUpdate"),
                postfix: new HarmonyMethod(
                    typeof(TelekinesisFeature),
                    nameof(FixedUpdatePostfix)
                )
            );

            harmony.Patch(
                AccessTools.PropertyGetter(
                    typeof(DungBall),
                    "CanBeGrabbed"
                ),
                postfix: new HarmonyMethod(
                    typeof(TelekinesisFeature),
                    nameof(CanBeGrabbedPostfix)
                )
            );

            MelonLogger.Msg(
                "[TelekinesisBall] Universal telekinesis initialized."
            );
        }

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
                ((Il2CppObjectBase)networkActor)
                .TryCast<BeetleActor>();

            return localActor != null && actor == localActor;
        }

        private static void UpdatePostfix(BeetleActor __instance)
        {
            try
            {
                if (!IsLocalActor(__instance))
                    return;

                _localActor = __instance;

                // F: Universal vacuum.
                bool fHeld = Input.GetKey(KeyCode.F);

                if (fHeld && !_vacuumKeyHeld)
                    StartVacuum(__instance);

                if (!fHeld && _vacuumKeyHeld)
                    StopVacuum();

                _vacuumKeyHeld = fHeld;

                // Left-click: Push balls or pull beetles.
                bool currentlyHeld = Input.GetMouseButton(0);

                if (!currentlyHeld)
                {
                    _mouseHeld = false;

                    if (_grappling)
                        StopGrapple();

                    return;
                }

                _mouseHeld = true;

                if (__instance.HasBall)
                {
                    if (_grappling)
                        StopGrapple();

                    return;
                }

                if (!_grappling)
                    TryStartGrapple(__instance);
            }
            catch (Exception ex)
            {
                MelonLogger.Error(
                    "[TelekinesisBall] Update error: " + ex
                );
            }
        }

        // -------------------------------------------------
        // F-KEY VACUUM
        // -------------------------------------------------

        private static void StartVacuum(BeetleActor actor)
        {
            try
            {
                if (actor == null || actor.HasBall)
                    return;

                Camera camera = Camera.main;

                if (camera == null)
                    return;

                Ray ray = new Ray(
                    camera.transform.position,
                    camera.transform.forward
                );

                RaycastHit[] hits = Physics.RaycastAll(
                    ray,
                    1000f,
                    Physics.DefaultRaycastLayers,
                    QueryTriggerInteraction.Ignore
                );

                Array.Sort(
                    hits,
                    (a, b) => a.distance.CompareTo(b.distance)
                );

                foreach (RaycastHit hit in hits)
                {
                    if (hit.collider == null)
                        continue;

                    if (hit.collider.transform.IsChildOf(actor.transform) ||
                        hit.collider.transform == actor.transform)
                        continue;

                    DungBall ball =
                        hit.collider.GetComponentInParent<DungBall>();

                    if (ball != null)
                    {
                        if (ball.RB == null)
                            continue;

                        _vacuumBall = ball;
                        _vacuumBeetle = null;
                        _vacuumActive = true;

                        MelonLogger.Msg(
                            "[TelekinesisBall] Vacuum attached to ball."
                        );

                        return;
                    }

                    BeetleActor beetle =
                        hit.collider.GetComponentInParent<BeetleActor>();

                    if (beetle != null && beetle != actor)
                    {
                        _vacuumBeetle = beetle;
                        _vacuumBall = null;
                        _vacuumActive = true;

                        MelonLogger.Msg(
                            "[TelekinesisBall] Vacuum attached to beetle."
                        );

                        return;
                    }
                }

                MelonLogger.Msg(
                    "[TelekinesisBall] Vacuum found no target."
                );
            }
            catch (Exception ex)
            {
                MelonLogger.Error(
                    "[TelekinesisBall] Vacuum start error: " + ex
                );

                StopVacuum();
            }
        }

        private static void StopVacuum()
        {
            if (!_vacuumActive)
                return;

            _vacuumActive = false;
            _vacuumBall = null;
            _vacuumBeetle = null;

            MelonLogger.Msg(
                "[TelekinesisBall] Vacuum released."
            );
        }

        private static Vector3 GetVacuumDestination()
        {
            Camera camera = Camera.main;

            if (camera == null || _localActor == null)
                return Vector3.zero;

            Vector3 direction = camera.transform.forward.normalized;

            return _localActor.transform.position
                + direction * HoldDistance
                + Vector3.up * HoldHeight;
        }

        private static void UpdateVacuum()
        {
            if (!_vacuumActive || !_vacuumKeyHeld)
                return;

            if (_localActor == null || _localActor.HasBall)
            {
                StopVacuum();
                return;
            }

            if (_vacuumBall != null)
            {
                HoldBallInFront();
                return;
            }

            if (_vacuumBeetle != null)
            {
                PullVacuumBeetle(
                    _localActor.transform.position
                );

                return;
            }

            StopVacuum();
        }

        private static void HoldBallInFront()
        {
            if (_vacuumBall == null)
            {
                StopVacuum();
                return;
            }

            Rigidbody rb = _vacuumBall.RB;

            if (rb == null)
            {
                StopVacuum();
                return;
            }

            Vector3 destination = GetVacuumDestination();

            if (destination == Vector3.zero)
                return;

            Vector3 difference =
                destination - rb.position;

            float distance = difference.magnitude;

            if (distance <= ArrivalDistance)
            {
                rb.velocity = Vector3.zero;
                return;
            }

            // Direct velocity control keeps the ball
            // positioned in front of the beetle.
            Vector3 desiredVelocity =
                difference * VacuumGain;

            desiredVelocity = Vector3.ClampMagnitude(
                desiredVelocity,
                MaxVacuumSpeed
            );

            rb.velocity = desiredVelocity;
        }

        private static void PullVacuumBeetle(Vector3 origin)
        {
            if (_vacuumBeetle == null)
            {
                StopVacuum();
                return;
            }

            Vector3 difference =
                origin - _vacuumBeetle.transform.position;

            if (difference.magnitude <= StopDistance)
                return;

            Vector3 velocity =
                difference.normalized * PullSpeed;

            _vacuumBeetle.SetLinearVelocity(velocity);
        }

        // -------------------------------------------------
        // LEFT-CLICK TELEKINESIS
        // -------------------------------------------------

        private static void TryStartGrapple(BeetleActor actor)
        {
            Camera camera = Camera.main;

            if (camera == null)
                return;

            Ray ray = new Ray(
                camera.transform.position,
                camera.transform.forward
            );

            RaycastHit[] hits = Physics.RaycastAll(
                ray,
                1000f,
                Physics.DefaultRaycastLayers,
                QueryTriggerInteraction.Ignore
            );

            Array.Sort(
                hits,
                (a, b) => a.distance.CompareTo(b.distance)
            );

            foreach (RaycastHit hit in hits)
            {
                if (hit.collider == null)
                    continue;

                if (hit.collider.transform.IsChildOf(actor.transform) ||
                    hit.collider.transform == actor.transform)
                    continue;

                DungBall ball =
                    hit.collider.GetComponentInParent<DungBall>();

                if (ball != null)
                {
                    _targetBall = ball;
                    _targetBeetle = null;
                    _grappling = true;

                    MelonLogger.Msg(
                        "[TelekinesisBall] Pushing ball."
                    );

                    return;
                }

                BeetleActor beetle =
                    hit.collider.GetComponentInParent<BeetleActor>();

                if (beetle != null && beetle != actor)
                {
                    _targetBeetle = beetle;
                    _targetBall = null;
                    _grappling = true;

                    MelonLogger.Msg(
                        "[TelekinesisBall] Attached to player."
                    );

                    return;
                }
            }

            if (Time.time >= _nextSearchLogTime)
            {
                MelonLogger.Msg(
                    "[TelekinesisBall] No ball or player found."
                );

                _nextSearchLogTime = Time.time + 1f;
            }
        }

        private static void FixedUpdatePostfix(BeetleActor __instance)
        {
            if (!IsLocalActor(__instance))
                return;

            try
            {
                UpdateVacuum();

                if (!_grappling || !_mouseHeld)
                    return;

                if (_localActor == null)
                    return;

                if (_localActor.HasBall)
                {
                    StopGrapple();
                    return;
                }

                Vector3 origin =
                    _localActor.transform.position;

                if (_targetBall != null)
                {
                    PushBall(origin);
                    return;
                }

                if (_targetBeetle != null)
                {
                    PullPlayer(origin);
                    return;
                }

                StopGrapple();
            }
            catch (Exception ex)
            {
                if (Time.time >= _nextErrorLogTime)
                {
                    MelonLogger.Error(
                        "[TelekinesisBall] Physics error: " + ex
                    );

                    _nextErrorLogTime = Time.time + 1f;
                }
            }
        }

        private static void PushBall(Vector3 origin)
        {
            if (_targetBall == null)
            {
                StopGrapple();
                return;
            }

            // Reverse the direction: push the ball away
            // from the local beetle instead of pulling it in.
            Vector3 difference =
                _targetBall.transform.position - origin;

            if (difference.sqrMagnitude < 0.001f)
                return;

            Vector3 force =
                difference.normalized * BallPullForce;

            if (_targetBall.ForceDispatcher == null)
                return;

            var parameters =
                new RigidbodyForceRpcDispatcher.Params(
                    force,
                    false,
                    true,
                    true,
                    false
                );

            _targetBall.ForceDispatcher
                .DispatchForceRpcOrPerformLocal(parameters);
        }

        private static void PullPlayer(Vector3 origin)
        {
            if (_targetBeetle == null)
            {
                StopGrapple();
                return;
            }

            Vector3 difference =
                origin - _targetBeetle.transform.position;

            if (difference.magnitude <= StopDistance)
            {
                StopGrapple();
                return;
            }

            Vector3 velocity =
                difference.normalized * PullSpeed;

            _targetBeetle.SetLinearVelocity(velocity);
        }

        private static void StopGrapple()
        {
            if (!_grappling)
                return;

            _grappling = false;
            _targetBall = null;
            _targetBeetle = null;

            MelonLogger.Msg(
                "[TelekinesisBall] Released target."
            );
        }
    }
}