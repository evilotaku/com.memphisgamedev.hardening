using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace MarketShopandRetailSystem.Multiplayer.EditorTools
{
    /// <summary>
    /// Tools &gt; Builds &gt; Include Test Menus: adds or removes the SHOPSIM_TEST_MENUS define on Standalone, which compiles
    /// the F8 garage and F9 yard debug menus (and their test drivers) into release builds. They are always in the
    /// Editor and development builds. Off by default: the menus unlock, teleport and finish jobs, so a build that
    /// players get must not have them. Every build made with the define on says so in the console.
    /// </summary>
    public class TestMenusBuildOption : IPreprocessBuildWithReport
    {
        public const string Define = "SHOPSIM_TEST_MENUS";
        private const string MenuPath = "Tools/Builds/Include Test Menus (F8, F9)";

        public int callbackOrder => 0;

        public static bool Enabled
        {
            get => Defines().Contains(Define);
            set
            {
                var defines = Defines().Where(d => d != Define).ToList();
                if (value) defines.Add(Define);
                PlayerSettings.SetScriptingDefineSymbols(NamedBuildTarget.Standalone, defines.ToArray());
            }
        }

        private static string[] Defines()
        {
            PlayerSettings.GetScriptingDefineSymbols(NamedBuildTarget.Standalone, out string[] defines);
            return defines;
        }

        [MenuItem(MenuPath, false)]
        private static void Toggle()
        {
            Enabled = !Enabled;
            Debug.Log("[Builds] Test menus (F8, F9) are now " + (Enabled ? "INCLUDED in" : "left out of") + " Standalone builds.");
        }

        [MenuItem(MenuPath, true)]
        private static bool ToggleValidate()
        {
            Menu.SetChecked(MenuPath, Enabled);
            return true;
        }

        public void OnPreprocessBuild(BuildReport report)
        {
            if (Enabled && report.summary.platformGroup == BuildTargetGroup.Standalone)
            {
                Debug.LogWarning("[Builds] This build includes the F8/F9 test menus (" + Define + "). Turn off Tools > Builds > Include Test Menus before making a build for players.");
            }
        }
    }
}
