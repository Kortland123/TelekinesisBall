
using HarmonyLib;
using Il2Cpp;
using MelonLoader;
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace TelekinesisBall
{
    public static class HeadbuttBuff
    {
        private const int AirHeadbuttLimit = 1_000_000;
        private const float CooldownMultiplier = 0.5f;
        private const float LungeMultiplier = 1.5f;
        private const float MinimumLungeSpeed = 20f;

        private static global::HarmonyLib.Harmony _harmony;

        private static readonly HashSet<int> ActiveHeadbutts =
            new HashSet<int>();

        private static readonly HashSet<int> ScaledHeadbutts =
            new HashSet<int>();

        private static readonly HashSet<int> LoggedCooldowns =
            new HashSet<int>();

        private static PropertyInfo _cooldownTimerProperty;
        private static PropertyInfo _durationProperty;
        private static FieldInfo _durationField;

        public static void Initialize(global::HarmonyLib.Harmony harmony)
        {
            _harmony = harmony;

            try
            {
                CacheTimerReflection();

                Patch(
                    typeof(BeetleActor),
                    "Update",
                    nameof(BeetleUpdatePostfix));

                Patch(
                    typeof(BeetleGeneralState_Headbutting),
                    "Enter",
                    nameof(HeadbuttEnterPostfix));

                Patch(
                    typeof(BeetleGeneralState_Headbutting),
                    "Exit",
                    nameof(HeadbuttExitPostfix));

                Patch(
                    typeof(BeetleActor),
                    "FixedUpdate",
                    nameof(BeetleFixedUpdatePostfix));

                MelonLogger.Msg(
                    "[TelekinesisBall] Headbutt buff initialized.");
            }
            catch (Exception ex)
            {
                MelonLogger.Error(
                    "[TelekinesisBall] Headbutt initialization failed: " + ex);
            }
        }

        private static void Patch(
            Type targetType,
            string methodName,
            string postfixName)
        {
            MethodInfo original =
                AccessTools.Method(targetType, methodName);

            if (original == null)
            {
                MelonLogger.Warning(
                    "[TelekinesisBall] Could not find " +
                    targetType.Name + "." + methodName);

                return;
            }

            _harmony.Patch(
                original,
                postfix: new HarmonyMethod(
                    typeof(HeadbuttBuff),
                    postfixName));

            MelonLogger.Msg(
                "[TelekinesisBall] Patched " +
                targetType.Name + "." + methodName);
        }

        private static void CacheTimerReflection()
        {
            _cooldownTimerProperty = AccessTools.Property(
                typeof(BeetleActor),
                "HeadbuttCooldownTimer");

            if (_cooldownTimerProperty == null)
            {
                MelonLogger.Warning(
                    "[HeadbuttBuff] Could not find cooldown timer property.");
                return;
            }

            Type timerType = _cooldownTimerProperty.PropertyType;

            _durationProperty = AccessTools.Property(
                timerType,
                "Duration");

            _durationField = AccessTools.Field(
                timerType,
                "_duration");

            MelonLogger.Msg(
                "[HeadbuttBuff] Timer reflection ready. " +
                "Duration property setter: " +
                (_durationProperty?.CanWrite ?? false) +
                " | Backing field: " +
                (_durationField != null));
        }

        private static void BeetleUpdatePostfix(BeetleActor __instance)
        {
            try
            {
                if (__instance == null)
                    return;

                __instance.maxAirHeadbutts = AirHeadbuttLimit;
                __instance._airHeadbuttsLeft = AirHeadbuttLimit;

                SetCooldownDuration(__instance);
            }
            catch (Exception ex)
            {
                MelonLogger.Error(
                    "[HeadbuttBuff] Beetle update error: " + ex);
            }
        }

        private static void SetCooldownDuration(BeetleActor actor)
        {
            try
            {
                if (_cooldownTimerProperty == null)
                    return;

                object timer =
                    _cooldownTimerProperty.GetValue(actor);

                if (timer == null)
                    return;

                float originalDuration = 2.75f;
                float newDuration =
                    originalDuration * CooldownMultiplier;

                bool changed = false;

                if (_durationProperty != null &&
                    _durationProperty.CanWrite)
                {
                    _durationProperty.SetValue(timer, newDuration);
                    changed = true;
                }
                else if (_durationField != null)
                {
                    _durationField.SetValue(timer, newDuration);
                    changed = true;
                }

                if (changed)
                {
                    int id = actor.gameObject.GetInstanceID();

                    if (LoggedCooldowns.Add(id))
                    {
                        MelonLogger.Msg(
                            "[HeadbuttBuff] ID=" + id +
                            " cooldown duration set to " +
                            newDuration + " seconds.");
                    }
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning(
                    "[HeadbuttBuff] Could not set cooldown: " +
                    ex.Message);
            }
        }

        private static void HeadbuttEnterPostfix(BeetleActor context)
        {
            try
            {
                if (context == null || context.gameObject == null)
                    return;

                int id = context.gameObject.GetInstanceID();

                ActiveHeadbutts.Add(id);
                ScaledHeadbutts.Remove(id);

                MelonLogger.Msg(
                    "[HeadbuttBuff] Headbutt started: " +
                    context.gameObject.name);
            }
            catch (Exception ex)
            {
                MelonLogger.Error(
                    "[HeadbuttBuff] Enter error: " + ex);
            }
        }

        private static void HeadbuttExitPostfix(BeetleActor context)
        {
            try
            {
                if (context == null || context.gameObject == null)
                    return;

                int id = context.gameObject.GetInstanceID();

                ActiveHeadbutts.Remove(id);
                ScaledHeadbutts.Remove(id);
            }
            catch (Exception ex)
            {
                MelonLogger.Error(
                    "[HeadbuttBuff] Exit error: " + ex);
            }
        }

        private static void BeetleFixedUpdatePostfix(BeetleActor __instance)
        {
            try
            {
                if (__instance == null || __instance.gameObject == null)
                    return;

                int id = __instance.gameObject.GetInstanceID();

                if (!ActiveHeadbutts.Contains(id))
                    return;

                if (ScaledHeadbutts.Contains(id))
                    return;

                Rigidbody body =
                    __instance.GetComponent<Rigidbody>();

                if (body == null)
                    return;

                Vector3 velocity = body.velocity;

                if (velocity.magnitude < MinimumLungeSpeed)
                    return;

                body.velocity = velocity * LungeMultiplier;

                ScaledHeadbutts.Add(id);

                MelonLogger.Msg(
                    "[HeadbuttBuff] Lunge boosted for " +
                    __instance.gameObject.name +
                    " | Previous speed: " +
                    velocity.magnitude.ToString("F2") +
                    " | New speed: " +
                    body.velocity.magnitude.ToString("F2"));
            }
            catch (Exception ex)
            {
                MelonLogger.Error(
                    "[HeadbuttBuff] Lunge boost error: " + ex);
            }
        }
    }
}
