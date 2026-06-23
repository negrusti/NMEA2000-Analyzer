namespace NMEA2000Analyzer
{
    internal static class RuntimeCleanup
    {
        public static void StopLiveServices()
        {
            TryStop(ActisenseSerialCapture.StopCapture);
            TryStop(ActisenseDllCapture.StopCapture);
            TryStop(MastervoltHidCapture.StopCapture);
            TryStop(PCAN.Shutdown);
        }

        private static void TryStop(Action action)
        {
            try
            {
                action();
            }
            catch
            {
                // Exit cleanup must not be blocked by device or driver shutdown failures.
            }
        }
    }
}
