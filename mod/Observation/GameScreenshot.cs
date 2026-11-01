using System;
using RimPilot.Mod.Bridge;
using RimPilot.Protocol;
using UnityEngine;
using Verse;

namespace RimPilot.Mod.Observation
{
    internal sealed class GameScreenshotBackend : IScreenshotBackend
    {
        private readonly Func<GameNativeRender?> render;
        private GameNativeRender? armed;
        internal GameScreenshotBackend(Func<GameNativeRender?> render) { this.render = render; }
        public FrameGeometry Geometry => new FrameGeometry(Time.frameCount, Screen.width, Screen.height, Prefs.UIScale);
        public CaptureRequestStamp Arm(Action<ICaptureTicket> consume)
        {
            armed = render();
            if (armed == null) throw new WireException(WireError.NotReady);
            return armed.Arm(ticket => consume(ticket));
        }
        public void Cancel() { GameNativeRender? owner = armed; armed = null; if (owner != null) owner.Cancel(); }
        public byte[] ReadRgb(CaptureSize size)
        {
            if (RenderTexture.active != null || Screen.width != size.Width || Screen.height != size.Height)
                throw new WireException(WireError.NotReady);
            Texture2D? texture = null;
            try
            {
                // Audited installed binding: RGB24, exactly one mip. Do not call Apply:
                // readback is CPU-only, no upload/mipmap generation/camera/target changes.
                texture = new Texture2D(size.Width, size.Height, TextureFormat.RGB24, false);
                texture.ReadPixels(new Rect(0, 0, size.Width, size.Height), 0, 0, false);
                byte[] raw = texture.GetRawTextureData();
                if (raw.Length != size.RawBytes) throw new WireException(WireError.Unavailable);
                return raw;
            }
            finally
            {
                // Only our transient texture. Immediate destruction prevents deferred native
                // texture accumulation; never destroy an asset or any game-owned object.
                if (texture != null) UnityEngine.Object.DestroyImmediate(texture);
            }
        }
    }
}
