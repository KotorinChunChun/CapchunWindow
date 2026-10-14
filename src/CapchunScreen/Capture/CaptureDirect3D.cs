using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX.Direct3D11;

namespace CapchunScreen.Capture;

/// <summary>Windows SDKのCOM ABIだけを扱う。各ポインターは取得した参照1件を所有する。</summary>
internal sealed unsafe class CaptureDirect3D : IDisposable
{
    private nint device;
    private nint context;
    private nint staging;
    private uint stagingWidth, stagingHeight;
    internal IDirect3DDevice ProjectedDevice { get; private set; } = null!;

    internal CaptureDirect3D()
    {
        try
        {
            int result = D3D11CreateDevice(0, 1 /* hardware */, 0, 0x20 /* BGRA */, 0, 0, 7, out device, out _, out context);
            if (result < 0)
            {
                Release(ref context);
                Release(ref device);
                result = D3D11CreateDevice(0, 5 /* WARP */, 0, 0x20, 0, 0, 7, out device, out _, out context);
            }
            Marshal.ThrowExceptionForHR(result);
            // WGC内部と呼出側のスレッドが同じ即時コンテキストを共有できるよう保護する。
            Guid multithreadInterface = new("9B7E4E00-342C-4106-A19F-4F2704F689F0");
            nint multithread = 0;
            try
            {
                Marshal.ThrowExceptionForHR(Marshal.QueryInterface(context, in multithreadInterface, out multithread));
                var protect = (delegate* unmanaged[Stdcall]<nint, int, int>)(*(nint**)multithread)[5];
                _ = protect(multithread, 1);
            }
            finally { Release(ref multithread); }
            Guid iid = new("54EC77FA-1377-44E6-8C32-88FD5F44C84C");
            nint dxgi = 0, inspectable = 0;
            try
            {
                Marshal.ThrowExceptionForHR(Marshal.QueryInterface(device, in iid, out dxgi));
                Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi, out inspectable));
                ProjectedDevice = WinRT.MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
            }
            finally { Release(ref inspectable); Release(ref dxgi); }
        }
        catch { Dispose(); throw; }
    }

    internal static GraphicsCaptureItem CreateWindowItem(nint window)
    {
        const string className = "Windows.Graphics.Capture.GraphicsCaptureItem";
        nint name = 0, factory = 0, item = 0;
        try
        {
            Marshal.ThrowExceptionForHR(WindowsCreateString(className, className.Length, out name));
            Guid interop = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
            Marshal.ThrowExceptionForHR(RoGetActivationFactory(name, in interop, out factory));
            Guid itemInterface = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
            var create = (delegate* unmanaged[Stdcall]<nint, nint, Guid*, nint*, int>)(*(nint**)factory)[3];
            Marshal.ThrowExceptionForHR(create(factory, window, &itemInterface, &item));
            return WinRT.MarshalInterface<GraphicsCaptureItem>.FromAbi(item);
        }
        finally
        {
            Release(ref item);
            Release(ref factory);
            if (name != 0) WindowsDeleteString(name);
        }
    }

    internal Bitmap ReadBitmap(IDirect3DSurface surface, int width, int height, Action checkCancellation)
    {
        if (width < 1 || height < 1 || width > 32766 || height > 32766)
            throw new InvalidOperationException("ウィンドウの取得サイズが対応範囲外です。");
        nint inspectable = 0, access = 0, texture = 0;
        bool mapped = false;
        try
        {
            inspectable = WinRT.MarshalInterface<IDirect3DSurface>.FromManaged(surface);
            Guid accessInterface = new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(inspectable, in accessInterface, out access));
            Guid textureInterface = new("6F15AAF2-D208-4E89-9AB4-489535D34F9C");
            var getInterface = (delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)(*(nint**)access)[3];
            Marshal.ThrowExceptionForHR(getInterface(access, &textureInterface, &texture));
            TextureDescription description;
            var getDescription = (delegate* unmanaged[Stdcall]<nint, TextureDescription*, void>)(*(nint**)texture)[10];
            getDescription(texture, &description);
            if (description.Format != 87 || description.Width < width || description.Height < height)
                throw new InvalidOperationException("WGCフレームの形式またはサイズが取得対象と一致しません。");
            EnsureStaging(description);
            var copy = (delegate* unmanaged[Stdcall]<nint, nint, nint, void>)(*(nint**)context)[47];
            copy(context, staging, texture);
            var flush = (delegate* unmanaged[Stdcall]<nint, void>)(*(nint**)context)[111];
            flush(context);
            MappedResource resource;
            var map = (delegate* unmanaged[Stdcall]<nint, nint, uint, uint, uint, MappedResource*, int>)(*(nint**)context)[14];
            long deadline = Environment.TickCount64 + 3000;
            while (true)
            {
                checkCancellation();
                int result = map(context, staging, 0, 1 /* read */, 0x100000 /* do not wait */, &resource);
                if (result != unchecked((int)0x887A000A)) { Marshal.ThrowExceptionForHR(result); break; }
                if (Environment.TickCount64 >= deadline) throw new TimeoutException("GPUから映像を読み出せませんでした。");
                Thread.Sleep(2);
            }
            mapped = true;
            if (resource.Data == 0 || resource.RowPitch < checked(width * 4))
                throw new InvalidOperationException("GPUから返された映像の行サイズが不正です。");
            var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            try
            {
                var bits = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                try
                {
                    for (int y = 0; y < height; y++)
                    {
                        checkCancellation();
                        byte* destination = (byte*)bits.Scan0 + y * bits.Stride;
                        Buffer.MemoryCopy((byte*)resource.Data + (long)y * resource.RowPitch, destination, Math.Abs(bits.Stride), width * 4L);
                        for (int x = 0; x < width; x++) destination[x * 4 + 3] = 255;
                    }
                }
                finally { bitmap.UnlockBits(bits); }
                return bitmap;
            }
            catch { bitmap.Dispose(); throw; }
        }
        finally
        {
            if (mapped)
            {
                var unmap = (delegate* unmanaged[Stdcall]<nint, nint, uint, void>)(*(nint**)context)[15];
                unmap(context, staging, 0);
            }
            Release(ref texture);
            Release(ref access);
            if (inspectable != 0) WinRT.MarshalInterface<IDirect3DSurface>.DisposeAbi(inspectable);
        }
    }

    private void EnsureStaging(TextureDescription description)
    {
        if (staging != 0 && stagingWidth == description.Width && stagingHeight == description.Height) return;
        Release(ref staging);
        description.Usage = 3; // D3D11_USAGE_STAGING
        description.BindFlags = 0;
        description.CpuAccessFlags = 0x20000; // D3D11_CPU_ACCESS_READ
        description.MiscFlags = 0;
        var create = (delegate* unmanaged[Stdcall]<nint, TextureDescription*, nint, nint*, int>)(*(nint**)device)[5];
        nint created = 0;
        Marshal.ThrowExceptionForHR(create(device, &description, 0, &created));
        staging = created;
        stagingWidth = description.Width;
        stagingHeight = description.Height;
    }

    public void Dispose()
    {
        Release(ref staging);
        try { ProjectedDevice?.Dispose(); }
        finally
        {
            ProjectedDevice = null!;
            Release(ref context);
            Release(ref device);
        }
    }

    private static void Release(ref nint pointer)
    {
        var value = pointer;
        pointer = 0;
        if (value != 0) Marshal.Release(value);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TextureDescription
    {
        public uint Width, Height, MipLevels, ArraySize, Format, SampleCount, SampleQuality, Usage, BindFlags, CpuAccessFlags, MiscFlags;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct MappedResource { public nint Data; public uint RowPitch, DepthPitch; }

    [DllImport("d3d11.dll")]
    private static extern int D3D11CreateDevice(nint adapter, uint driver, nint software, uint flags, nint levels, uint levelCount,
        uint sdkVersion, out nint device, out uint featureLevel, out nint context);
    [DllImport("d3d11.dll")] private static extern int CreateDirect3D11DeviceFromDXGIDevice(nint device, out nint graphicsDevice);
    [DllImport("combase.dll", CharSet = CharSet.Unicode)] private static extern int WindowsCreateString(string value, int length, out nint handle);
    [DllImport("combase.dll")] private static extern int WindowsDeleteString(nint handle);
    [DllImport("combase.dll")] private static extern int RoGetActivationFactory(nint classId, in Guid interfaceId, out nint factory);
}
