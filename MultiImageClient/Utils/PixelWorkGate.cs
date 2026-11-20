#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;

namespace MultiImageClient
{
    // Runs full-size pixel work (contact sheets, card previews, image
    // conversions) one task at a time per process. Decoded bitmaps live in
    // native memory outside the GC heap limit; several jobs finishing together
    // used to decode at once and push Vibecoders past memory.high, where the
    // liveness guard ends the process and every running job with it.
    // Work already inside the gate on the same logical flow passes through,
    // so nested gated calls cannot deadlock.
    public static class PixelWorkGate
    {
        private static readonly SemaphoreSlim Gate = new(1, 1);
        private static readonly AsyncLocal<bool> Held = new();

        public static async Task<T> RunAsync<T>(Func<Task<T>> work)
        {
            if (Held.Value) return await work();
            await Gate.WaitAsync();
            try
            {
                Held.Value = true;
                return await work();
            }
            finally
            {
                Held.Value = false;
                Gate.Release();
            }
        }

        public static async Task RunAsync(Func<Task> work)
            => await RunAsync(async () => { await work(); return true; });

        public static T Run<T>(Func<T> work)
        {
            if (Held.Value) return work();
            Gate.Wait();
            try
            {
                Held.Value = true;
                return work();
            }
            finally
            {
                Held.Value = false;
                Gate.Release();
            }
        }
    }
}
