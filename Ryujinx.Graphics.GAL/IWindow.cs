using System;

namespace Ryujinx.Graphics.GAL
{
    public interface IWindow
    {
        void Present(ITexture texture, ImageCrop crop, Action swapBuffersCallback);

        void SetSize(int width, int height);

        void ChangeVSyncMode(bool vsyncEnabled);

        void SetAntiAliasing(AntiAliasing antialiasing);

        void SetUpscaler(UpscaleType type);

        void SetUpscalerLevel(float level);
    }
}
