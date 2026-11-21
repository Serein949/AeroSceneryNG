using System;

namespace AeroSceneryNG.Core
{
    /// <summary>Which simulator family the generated scenery targets.</summary>
    public enum SceneryTarget
    {
        /// <summary>Aerofly FS 4 on desktop: DXT1/BC1 TTC files.</summary>
        Fs4,

        /// <summary>Aerofly FS Global on Android/iOS: ETC2 TTC files packaged as .tme.</summary>
        Fsg,

        /// <summary>Both formats in one run (separate output trees, one shared download pass).</summary>
        Both
    }

    public static class SceneryTargets
    {
        public static bool WantsFs4(this SceneryTarget target)
        {
            return target == SceneryTarget.Fs4 || target == SceneryTarget.Both;
        }

        public static bool WantsFsg(this SceneryTarget target)
        {
            return target == SceneryTarget.Fsg || target == SceneryTarget.Both;
        }

        public static string DisplayName(this SceneryTarget target)
        {
            switch (target)
            {
                case SceneryTarget.Fsg: return Loc.T("Aerofly FS Global（手机端 · ETC2）");
                case SceneryTarget.Both: return Loc.T("同时生成 FS4 + FSG");
                default: return Loc.T("Aerofly FS 4（电脑端 · DXT1）");
            }
        }

        public static int MaxAfLevel(this SceneryTarget target)
        {
            // FSG currently streams levels 9..14; level 15 textures are not loaded by the mobile
            // renderer and only waste space. FS4 accepts the full 9..15 range. A combined run has
            // to use the intersection, otherwise the FSG half contains tiles the phone ignores.
            return target == SceneryTarget.Fs4 ? 15 : 14;
        }

        public static int MaxImageZoom(this SceneryTarget target)
        {
            // Zoom 18 textures are not useful on FSG; a combined run shares one download zoom.
            return target == SceneryTarget.Fs4 ? 19 : 17;
        }
    }
}
