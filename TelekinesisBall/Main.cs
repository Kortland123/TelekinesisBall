using MelonLoader;
using HarmonyLib;

[assembly: MelonInfo(
    typeof(TelekinesisBall.Main),
    "TelekinesisBall",
    "1.0.0",
    "Kortland123"
)]

namespace TelekinesisBall
{
    public class Main : MelonMod
    {
        private global::HarmonyLib.Harmony _harmony;

        public override void OnInitializeMelon()
        {
            _harmony = new global::HarmonyLib.Harmony(
                "Kortland123.TelekinesisBall"
            );

            TelekinesisFeature.Initialize(_harmony);

            MelonLogger.Msg(
                "[TelekinesisBall] Mod successfully initialized."
            );
        }
    }
}