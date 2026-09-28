namespace ScrollWheelGuard
{
    /// <summary>
    /// Stock KSP game-parameter page for Scroll Wheel Guard.
    ///
    /// This appears in the normal Difficulty Options / custom settings list and is persisted by KSP
    /// with the current save.  Runtime code reads the value through ApplyToRuntime so changes take
    /// effect immediately without requiring a scene change or restart.
    /// </summary>
    public class ScrollGuardSettings : GameParameters.CustomParameterNode
    {
        public override string Title
        {
            get { return "Settings"; }
        }

        public override GameParameters.GameMode GameMode
        {
            get { return GameParameters.GameMode.ANY; }
        }

        public override string Section
        {
            get { return "Scroll Wheel Guard"; }
        }

        public override string DisplaySection
        {
            get { return "Scroll Wheel Guard"; }
        }

        public override int SectionOrder
        {
            get { return 0; }
        }

        public override bool HasPresets
        {
            get { return false; }
        }

        [GameParameters.CustomParameterUI(
            "Block scroll wheel when mouse is outside the KSP window",
            toolTip = "Prevents mouse-wheel input from reaching KSP while the mouse pointer is outside the entire KSP window.",
            autoPersistance = true)]
        public bool blockOutsideGameWindow = true;

        [GameParameters.CustomParameterUI(
            "Disable all logging except errors",
            toolTip = "Suppresses Scroll Wheel Guard informational and warning messages. Error messages will still be written to the KSP log.",
            autoPersistance = true)]
        public bool errorsOnlyLogging = false;

        internal static void ApplyToRuntime()
        {
            // There is no CurrentGame at the main menu and during a few startup/shutdown transitions.
            // Keep the runtime default in those cases.
            if (HighLogic.CurrentGame == null || HighLogic.CurrentGame.Parameters == null)
                return;

            try
            {
                ScrollGuardSettings settings =
                    HighLogic.CurrentGame.Parameters.CustomParams<ScrollGuardSettings>();

                if (settings != null)
                {
                    ScrollGuard.BlockOutsideGameWindow = settings.blockOutsideGameWindow;
                    ScrollGuard.ErrorsOnlyLogging = settings.errorsOnlyLogging;
                }
            }
            catch
            {
                // A settings lookup must never interfere with input processing.  If KSP is between
                // game states, retain the last successfully-applied runtime value.
            }
        }
    }
}
