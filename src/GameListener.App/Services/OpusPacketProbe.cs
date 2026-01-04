using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace GameListener.App.Services
{
    static class OpusPacketProbe
    {
        [DllImport("opus", CallingConvention = CallingConvention.Cdecl)]
        private static extern int opus_packet_get_nb_samples(
            IntPtr packet, int len, int Fs);

        public static bool TryGetSamples(ReadOnlySpan<byte> packet, int sampleRate, out int samplesPerChannel)
        {
            samplesPerChannel = 0;
            if (packet.Length <= 0) return false;

            unsafe
            {
                fixed (byte* p = packet)
                {
                    int res = opus_packet_get_nb_samples((IntPtr)p, packet.Length, sampleRate);
                    if (res < 0) return false;
                    samplesPerChannel = res;
                    return true;
                }
            }
        }
    }
}
