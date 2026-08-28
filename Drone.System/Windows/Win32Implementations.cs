#pragma warning disable CA1416, CS8604
using global::System.Diagnostics;
using global::System.Runtime.InteropServices;
using global::System.Text;
using Drone.Core;

namespace Drone.System.Windows;

/// <summary>
/// Hardware-accelerated screen capture using DXGI Desktop Duplication API.
/// Available on Windows 8+. Falls back to GDI BitBlt if DXGI unavailable.
/// Ported from Velocity-Remote's DxgiScreenCapture.
/// </summary>
public class Win32ScreenCapture : IScreenCapture, IDisposable
{
    private readonly ILogger _logger;
    private readonly DxgiCapture? _dxgi;
    private bool _disposed;

    public Win32ScreenCapture(ILogger logger)
    {
        _logger = logger;
        try
        {
            _dxgi = new DxgiCapture(logger);
            if (_dxgi.IsAvailable)
                _logger.LogInformation("DXGI Desktop Duplication initialized: {W}x{H}", _dxgi.Width, _dxgi.Height);
            else
                _logger.LogWarning("DXGI not available, using GDI fallback");
        }
        catch (Exception ex)
        {
            _logger.LogWarning("DXGI init failed: {Error}. Using GDI fallback.", ex.Message);
        }
    }

    public bool IsDxgiAvailable => _dxgi?.IsAvailable == true;
    
    public Task<byte[]> CaptureScreenAsync(CancellationToken ct = default)
    {
        if (_dxgi?.IsAvailable == true)
        {
            var data = _dxgi.CaptureFrameBmp();
            if (data != null) return Task.FromResult(data);
        }
        // GDI fallback
        return Task.FromResult(CaptureScreenGdi());
    }

    public Task<byte[]> CaptureWindowAsync(nint handle, CancellationToken ct = default)
    {
        if (handle == nint.Zero) return Task.FromResult(Array.Empty<byte>());
        // Window capture always uses GDI (DXGI captures the whole desktop)
        return Task.FromResult(CaptureWindowGdi(handle));
    }

    public Task<(int Width, int Height)> GetScreenSizeAsync()
    {
        if (_dxgi?.IsAvailable == true)
            return Task.FromResult((_dxgi.Width, _dxgi.Height));
        return Task.FromResult((GetSystemMetrics(0), GetSystemMetrics(1)));
    }

    public Task<(byte R, byte G, byte B)> GetPixelColorAsync(int x, int y)
    {
        var hdc = GetWindowDC(GetDesktopWindow());
        try
        {
            var color = GetPixel(hdc, x, y);
            return Task.FromResult(((byte)(color & 0xFF), (byte)((color >> 8) & 0xFF), (byte)((color >> 16) & 0xFF)));
        }
        finally { ReleaseDC(GetDesktopWindow(), hdc); }
    }


    /// <summary>Capture raw BGRA pixels directly from DXGI. For delta pipeline use.</summary>
    public (byte[] Pixels, uint Stride, int Width, int Height)? CaptureFrameBgraDirect()
    {
        // Try DXGI first
        var dxgiResult = _dxgi?.CaptureFrameBgra();
        if (dxgiResult != null) return dxgiResult;
        
        // GDI fallback: capture screen as raw BGRA via GDI
        return CaptureRawBgraGdi();
    }

    /// <summary>IScreenCapture raw BGRA capture. Works with both DXGI and GDI.</summary>
    public Task<(byte[] Pixels, uint Stride, int Width, int Height)?> CaptureRawBgraAsync(CancellationToken ct = default)
    {
        return Task.FromResult(CaptureFrameBgraDirect());
    }

    // Cached DC for ultra-fast pixel sampling
    private nint _sampleDc;
    private readonly int[] _sampleOffsets = new int[64];
    private bool _samplesInitialized;
    private readonly uint[] _lastSamplePixels = new uint[64];
    private int _sampleCallCount;
    
    public bool HasScreenChanged()
    {
        int w = GetSystemMetrics(0);
        int h = GetSystemMetrics(1);
        if (w <= 0 || h <= 0) return true;
        
        if (!_samplesInitialized)
        {
            int idx = 0;
            for (int row = 0; row < 8; row++)
                for (int col = 0; col < 8; col++)
                {
                    _sampleOffsets[idx] = ((row + 1) * h / 9) * w + ((col + 1) * w / 9);
                    idx++;
                }
            _samplesInitialized = true;
        }
        
        // Cache the DC - release every 100 calls to avoid stale DC
        if (_sampleDc == nint.Zero || (_sampleCallCount++ % 100) == 0)
        {
            if (_sampleDc != nint.Zero) ReleaseDC(GetDesktopWindow(), _sampleDc);
            _sampleDc = GetWindowDC(GetDesktopWindow());
        }
        
        int changedCount = 0;
        for (int i = 0; i < 64; i++)
        {
            uint color = GetPixel(_sampleDc, _sampleOffsets[i] % w, _sampleOffsets[i] / w);
            if (color != _lastSamplePixels[i])
            {
                changedCount++;
                _lastSamplePixels[i] = color;
            }
        }
        return changedCount > 2;
    }

        /// <summary>Zero-alloc: capture raw BGRA directly into pre-allocated buffer.</summary>
    public Task<(uint Stride, int Width, int Height)?> CaptureRawBgraAsync(byte[] targetBuffer, CancellationToken ct = default)
    {
        var result = CaptureRawBgraIntoBuffer(targetBuffer);
        return Task.FromResult(result);
    }

    private (uint Stride, int Width, int Height)? CaptureRawBgraIntoBuffer(byte[] target)
    {
        // Try DXGI first (smart: returns null = no changes)
        if (_dxgi?.IsAvailable == true)
        {
            var dxgiResult = _dxgi.CaptureFrameBgraInto(target);
            if (dxgiResult != null) return dxgiResult;
            return null; // DXGI says no changes
        }
        // GDI fallback
        return CaptureRawBgraGdiInto(target);
    }

    /// <summary>GDI BGRA capture directly into pre-allocated buffer. Zero-alloc.</summary>
    private (uint Stride, int Width, int Height)? CaptureRawBgraGdiInto(byte[] target)
    {
        try
        {
            int w = GetSystemMetrics(0);
            int h = GetSystemMetrics(1);
            if (w <= 0 || h <= 0) return null;
            int stride = w * 4;
            int needed = stride * h;
            if (target.Length < needed) return null;

            var hdcWindow = GetWindowDC(GetDesktopWindow());
            var hdcMem = CreateCompatibleDC(hdcWindow);
            var hbm = CreateCompatibleBitmap(hdcWindow, w, h);
            var hbmOld = SelectObject(hdcMem, hbm);
            BitBlt(hdcMem, 0, 0, w, h, hdcWindow, 0, 0, SRCCOPY);

            var bi = new BITMAPINFOHEADER
            {
                biSize = 40, biWidth = w, biHeight = -h,
                biPlanes = 1, biBitCount = 32, biCompression = 0
            };
            GetDIBits(hdcMem, hbm, 0, (uint)h, target, ref bi, 0);

            SelectObject(hdcMem, hbmOld);
            DeleteObject(hbm);
            DeleteDC(hdcMem);
            ReleaseDC(GetDesktopWindow(), hdcWindow);
            return ((uint)stride, w, h);
        }
        catch { return null; }
    }

    /// <summary>Capture raw BGRA pixels via GDI. No BMP header, just raw pixels.</summary>
    private (byte[] Pixels, uint Stride, int Width, int Height)? CaptureRawBgraGdi()
    {
        try
        {
            int w = GetSystemMetrics(0); // SM_CXSCREEN
            int h = GetSystemMetrics(1); // SM_CYSCREEN
            if (w <= 0 || h <= 0) return null;

            var hdcWindow = GetWindowDC(GetDesktopWindow());
            var hdcMem = CreateCompatibleDC(hdcWindow);
            var hbm = CreateCompatibleBitmap(hdcWindow, w, h);
            var hbmOld = SelectObject(hdcMem, hbm);

            BitBlt(hdcMem, 0, 0, w, h, hdcWindow, 0, 0, SRCCOPY);

            // Read as 32-bit BGRA
            var bi = new BITMAPINFOHEADER
            {
                biSize = 40, biWidth = w, biHeight = -h, // top-down
                biPlanes = 1, biBitCount = 32, biCompression = 0
            };
            int stride = w * 4;
            var pixels = new byte[stride * h];
            GetDIBits(hdcMem, hbm, 0, (uint)h, pixels, ref bi, 0);

            SelectObject(hdcMem, hbmOld);
            DeleteObject(hbm);
            DeleteDC(hdcMem);
            ReleaseDC(GetDesktopWindow(), hdcWindow);

            return (pixels, (uint)stride, w, h);
        }
        catch
        {
            return null;
        }
    }
    public void Dispose()
    {
        if (!_disposed) { _disposed = true; _dxgi?.Dispose(); }
    }

    // ── GDI Fallback ──────────────────────────────────────────────

    [DllImport("user32.dll")] private static extern nint GetDesktopWindow();
    [DllImport("user32.dll")] private static extern nint GetWindowDC(nint hWnd);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint hdc);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleBitmap(nint hdc, int w, int h);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint hdc, nint obj);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(nint hdc, int x, int y, int w, int h, nint src, int sx, int sy, uint op);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint hdc);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint hWnd, nint hDC);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("gdi32.dll")] private static extern int GetDIBits(nint hdc, nint hbmp, uint start, uint lines, byte[]? bits, ref BITMAPINFOHEADER bi, uint usage);
    [DllImport("gdi32.dll")] private static extern uint GetPixel(nint hdc, int x, int y);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hWnd, out GdiRect lpRect);
    [DllImport("kernel32.dll")] private static extern void CopyMemory(nint dest, nint src, uint count);

    private const uint SRCCOPY = 0x00CC0020;
    private const uint DIB_RGB_COLORS = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize; public int biWidth; public int biHeight;
        public ushort biPlanes; public ushort biBitCount; public uint biCompression;
        public uint biSizeImage; public int biXPelsPerMeter; public int biYPelsPerMeter;
        public uint biClrUsed; public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GdiRect { public int Left, Top, Right, Bottom; }

    private static byte[] CaptureScreenGdi()
    {
        var hdcScreen = GetWindowDC(GetDesktopWindow());
        try
        {
            var hdcMem = CreateCompatibleDC(hdcScreen);
            try
            {
                var width = GetSystemMetrics(0);
                var height = GetSystemMetrics(1);
                var hBitmap = CreateCompatibleBitmap(hdcScreen, width, height);
                try
                {
                    var hOld = SelectObject(hdcMem, hBitmap);
                    BitBlt(hdcMem, 0, 0, width, height, hdcScreen, 0, 0, SRCCOPY);
                    SelectObject(hdcMem, hOld);
                    return ExtractBmpData(hdcMem, hBitmap, width, height);
                }
                finally { DeleteObject(hBitmap); }
            }
            finally { DeleteDC(hdcMem); }
        }
        finally { ReleaseDC(GetDesktopWindow(), hdcScreen); }
    }

    private static byte[] CaptureWindowGdi(nint handle)
    {
        GetWindowRect(handle, out var rect);
        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0) return Array.Empty<byte>();

        var hdcScreen = GetWindowDC(handle);
        try
        {
            var hdcMem = CreateCompatibleDC(hdcScreen);
            try
            {
                var hBitmap = CreateCompatibleBitmap(hdcScreen, width, height);
                try
                {
                    var hOld = SelectObject(hdcMem, hBitmap);
                    BitBlt(hdcMem, 0, 0, width, height, hdcScreen, 0, 0, SRCCOPY);
                    SelectObject(hdcMem, hOld);
                    return ExtractBmpData(hdcMem, hBitmap, width, height);
                }
                finally { DeleteObject(hBitmap); }
            }
            finally { DeleteDC(hdcMem); }
        }
        finally { ReleaseDC(handle, hdcScreen); }
    }

    private static byte[] ExtractBmpData(nint hdcMem, nint hBitmap, int width, int height)
    {
        var bi = new BITMAPINFOHEADER { biSize = 40, biWidth = width, biHeight = -height, biPlanes = 1, biBitCount = 24, biCompression = 0 };
        var rowSize = ((width * 3 + 3) / 4) * 4;
        var pixelData = new byte[rowSize * height];
        GetDIBits(hdcMem, hBitmap, 0, (uint)height, pixelData, ref bi, DIB_RGB_COLORS);

        var fileSize = 14 + 40 + pixelData.Length;
        var bmp = new byte[fileSize];
        bmp[0] = (byte)'B'; bmp[1] = (byte)'M';
        var fl = fileSize;
        bmp[2] = (byte)fl; bmp[3] = (byte)(fl >> 8); bmp[4] = (byte)(fl >> 16); bmp[5] = (byte)(fl >> 24);
        var dataOffset = 54;
        bmp[10] = (byte)dataOffset; bmp[11] = (byte)(dataOffset >> 8); bmp[12] = (byte)(dataOffset >> 16); bmp[13] = (byte)(dataOffset >> 24);
        bmp[14] = 40;
        var w = width;
        bmp[18] = (byte)w; bmp[19] = (byte)(w >> 8); bmp[20] = (byte)(w >> 16); bmp[21] = (byte)(w >> 24);
        var h = height;
        bmp[22] = (byte)h; bmp[23] = (byte)(h >> 8); bmp[24] = (byte)(h >> 16); bmp[25] = (byte)(h >> 24);
        bmp[26] = 1; bmp[28] = 24;
        Array.Copy(pixelData, 0, bmp, 54, pixelData.Length);
        return bmp;
    }
}

// ── DXGI Desktop Duplication ────────────────────────────────────────

#region COM Interfaces

[ComImport]
[Guid("aec22fb8-76f3-4639-9be0-28eb43a67a2e")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDXGIOutput1
{
    void GetDesc(out DXGI_OUTPUT_DESC desc);
    void GetDisplayModeList(uint enumType, uint flags, ref uint numModes, nint modes);
    void FindClosestMode(ref DXGI_MODE_DESC modeToMatch, out DXGI_MODE_DESC closestMatch);
    void WaitForVBlank();
    void ReleaseOwnership();
    void AcquireOwnership(nint device);
    void GetParent([In] ref Guid riid, [Out, MarshalAs(UnmanagedType.Interface)] out object parent);
    void DuplicateOutput(nint device, out IDXGIOutputDuplication outputDuplication);
}

[ComImport]
[Guid("00cddea8-b2a8-4d3f-a8e2-b08e61a2b9cd")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDXGIAdapter1
{
    void GetParent([In] ref Guid riid, [Out] out nint parent);
    int EnumOutputs(uint outputIndex, out nint output);
    void GetDesc1(out DXGI_ADAPTER_DESC1 desc);
    int CheckInterfaceSupport([In] ref Guid interfaceName, out long umdVersion);
    uint AddRef();
    uint Release();
}

[ComImport]
[Guid("770aae78-f26f-4dba-a829-253c83d1b387")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDXGIFactory1
{
    void EnumAdapters(uint adapterIndex, out nint adapter);
    void MakeWindowAssociation(nint windowHandle, uint flags);
    void GetWindowAssociation(out nint windowHandle);
    int CreateSwapChain(nint device, ref nint desc, out nint swapChain);
    int CreateSoftwareAdapter(nint module, out nint adapter);
    int EnumAdapters1(uint adapterIndex, out IDXGIAdapter1 adapter);
    int IsCurrent();
    uint AddRef();
    uint Release();
}

[ComImport]
[Guid("191cfac3-a341-470d-b26e-a864f428319c")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDXGIOutputDuplication
{
    void GetDesc(out DXGI_OUTDUPL_DESC desc);
    [PreserveSig]
    int AcquireNextFrame(uint timeoutInMs, out DXGI_OUTDUPL_FRAME_INFO frameInfo, out IDXGIResource desktopResource);
    void GetFrameDirtyRects(uint bufferSize, nint dirtyRectsBuffer, out uint bufferSizeRequired);
    void GetFrameMoveRects(uint bufferSize, nint moveRectsBuffer, out uint bufferSizeRequired);
    void GetFramePointerShape(uint bufferSize, nint pointerShapeBuffer, out uint bufferSizeRequired, out DXGI_OUTDUPL_POINTER_SHAPE_INFO pointerShapeInfo);
    void MapDesktopSurface(out DXGI_MAPPED_RECT lockedRect);
    void UnMapDesktopSurface();
    void ReleaseFrame();
    [PreserveSig]
    int QueryInterface([In] ref Guid riid, out nint ppvObject);
    uint AddReference();
    uint Release();
}

[ComImport]
[Guid("0359057a-b86b-4b9f-9f5b-4c6f49deca4b")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDXGIResource
{
    void GetSharedHandle(out nint sharedHandle);
    void GetUsage(out uint usage);
    void SetEvictionPriority(uint evictionPriority);
    void GetEvictionPriority(out uint evictionPriority);
    [PreserveSig]
    int QueryInterface([In] ref Guid riid, out nint ppvObject);
    uint AddReference();
    uint Release();
}

[ComImport]
[Guid("6f15aaf2-d208-4e89-9ab4-489535d34f9c")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ID3D11Texture2D
{
    void GetType(out uint type);
    void GetWidth(out uint width);
    void GetHeight(out uint height);
    uint AddRef();
    uint Release();
}

[ComImport]
[Guid("c0bfa96c-e089-44fb-8eaf-26f8796190da")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ID3D11DeviceContext
{
    void CopyResource(ID3D11Texture2D dstResource, ID3D11Texture2D srcResource);
    void Map(ID3D11Texture2D pResource, uint subresource, uint mapType, uint mapFlags, out D3D11_MAPPED_SUBRESOURCE mappedResource);
    void Unmap(ID3D11Texture2D pResource, uint subresource);
}

[ComImport]
[Guid("db6f6ddb-ac77-4e88-8253-819df9bbf140")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ID3D11Device
{
    [PreserveSig]
    int CreateTexture2D(ref D3D11_TEXTURE2D_DESC desc, nint initialData, out ID3D11Texture2D texture2D);
    void GetImmediateContext(out ID3D11DeviceContext context);
}

#endregion

#region DXGI Structures

[StructLayout(LayoutKind.Sequential)]
internal struct DXGI_OUTPUT_DESC
{
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
    public string DeviceName;
    public DxgiRect DesktopCoordinates;
    public int AttachedToDesktop;
    public uint Rotation;
    public nint Monitor;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DxgiRect { public int Left, Top, Right, Bottom; public int Width => Right - Left; public int Height => Bottom - Top; }

[StructLayout(LayoutKind.Sequential)]
internal struct DXGI_MODE_DESC
{
    public uint Width, Height;
    public uint RefreshRateNumerator, RefreshRateDenominator;
    public uint Format, ScanlineOrdering, Scaling;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DXGI_OUTDUPL_DESC
{
    public DXGI_MODE_DESC ModeDesc;
    public uint Rotation;
    public int DesktopImageInSystemMemory;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DXGI_OUTDUPL_FRAME_INFO
{
    public long LastPresentTime, LastMouseUpdateTime;
    public int AccumulatedFrames, RectsCoalesced, ProtectedContentMaskedOut;
    public int PointerPositionBufferSize, TotalMetadataBufferSize, PointerShapeBufferSize;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DXGI_OUTDUPL_POINTER_SHAPE_INFO
{
    public uint Type, Width, Height, Pitch;
    public POINT HotSpot;
}

[StructLayout(LayoutKind.Sequential)]
internal struct POINT { public int X, Y; }

[StructLayout(LayoutKind.Sequential)]
internal struct DXGI_MAPPED_RECT { public int Pitch; public nint pBits; }

[StructLayout(LayoutKind.Sequential)]
internal struct D3D11_MAPPED_SUBRESOURCE { public nint pData; public uint RowPitch; public uint DepthPitch; }

[StructLayout(LayoutKind.Sequential)]
internal struct D3D11_TEXTURE2D_DESC
{
    public uint Width, Height, MipLevels, ArraySize, Format;
    public DXGI_SAMPLE_DESC SampleDesc;
    public uint Usage, BindFlags, CPUAccessFlags, MiscFlags;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DXGI_SAMPLE_DESC { public uint Count, Quality; }

[StructLayout(LayoutKind.Sequential)]
internal struct DXGI_ADAPTER_DESC1
{
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
    public string Description;
    public uint VendorId, DeviceId, SubSysId, Revision;
    public ulong DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
    public long AdapterLuid;
    public uint Flags;
}

#endregion

/// <summary>
/// DXGI Desktop Duplication capture engine.
/// Captures screen via D3D11 GPU texture → staging → CPU pixel copy.
/// Much faster than GDI BitBlt for full-screen captures.
/// </summary>
internal sealed class DxgiCapture : IDisposable
{
    private const uint DXGI_FORMAT_B8G8R8A8_UNORM = 87;
    private const uint D3D11_USAGE_STAGING = 3;
    private const uint D3D11_CPU_ACCESS_READ = 0x20000;
    private const uint D3D11_MAP_READ = 1;
    private const int S_OK = 0;
    private const int DXGI_ERROR_WAIT_TIMEOUT = -2020998655;

    [DllImport("d3d11.dll")]
    private static extern int D3D11CreateDevice(nint adapter, uint driverType, nint software, uint flags, uint[] featureLevels, uint featureLevelsCount, uint sdkVersion, out nint device, out uint featureLevel, out nint deviceContext);

    [DllImport("dxgi.dll")]
    private static extern int CreateDXGIFactory1(ref Guid riid, out nint factory);

    [DllImport("kernel32.dll")]
    private static extern void CopyMemory(nint dest, nint src, uint count);

    private nint _d3dDevice;
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private IDXGIOutputDuplication? _outputDuplication;
    private ID3D11Texture2D? _stagingTexture;
    private nint _dxgiFactory;
    private bool _disposed;

    public bool IsAvailable { get; private set; }
    public int Width { get; private set; }
    public int Height { get; private set; }

    private readonly ILogger _logger;

    public DxgiCapture(ILogger logger)
    {
        _logger = logger;
        try
        {
            Initialize();
            IsAvailable = true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("DXGI capture unavailable: {Error}", ex.Message);
            IsAvailable = false;
        }
    }

    private void Initialize()
    {
        uint[] featureLevels = { 0xb000, 0xa100 }; // D3D_FEATURE_LEVEL_11_0, 10_1
        int hr = D3D11CreateDevice(nint.Zero, 1, nint.Zero, 0x2 /* D3D11_CREATE_DEVICE_BGRA_SUPPORT */, featureLevels, (uint)featureLevels.Length, 7, out _d3dDevice, out _, out _);
        if (hr != 0)
            {
                _logger.LogWarning("D3D11CreateDevice failed with HRESULT=0x{HR:X8}", hr);
                throw new COMException("D3D11CreateDevice failed", hr);
            }

        _device = (ID3D11Device)Marshal.GetObjectForIUnknown(_d3dDevice);
        _device.GetImmediateContext(out _context);

        var factoryGuid = new Guid("770aae78-f26f-4dba-a829-253c83d1b387");
        hr = CreateDXGIFactory1(ref factoryGuid, out _dxgiFactory);
        if (hr != 0) throw new COMException("CreateDXGIFactory1 failed", hr);

        var factory = (IDXGIFactory1)Marshal.GetObjectForIUnknown(_dxgiFactory);

        // Enumerate adapters to find one supporting Desktop Duplication
        for (uint adapterIdx = 0; ; adapterIdx++)
        {
            int adapterHr = factory.EnumAdapters1(adapterIdx, out var adapter);
            if (adapterHr != 0) break;

            try
            {
                adapter.GetDesc1(out var desc);
                _logger.LogDebug("DXGI adapter {Idx}: {Name} ({VRAM}MB)", adapterIdx, desc.Description, desc.DedicatedVideoMemory / 1024 / 1024);

                for (uint outputIdx = 0; ; outputIdx++)
                {
                    int outputHr = adapter.EnumOutputs(outputIdx, out var outputPtr);
                    if (outputHr != 0) break;

                    try
                    {
                        var output = (IDXGIOutput1)Marshal.GetObjectForIUnknown(outputPtr);
                        output.GetDesc(out var outputDesc);

                        try
                        {
                            output.DuplicateOutput(_d3dDevice, out var duplication);
                            Width = outputDesc.DesktopCoordinates.Width;
                            Height = outputDesc.DesktopCoordinates.Height;
                            _outputDuplication = duplication;
                            _logger.LogDebug("DXGI output {Idx}: {W}x{H}", outputIdx, Width, Height);
                            
                            // Eagerly create staging texture and populate with initial frame
                            CreateStagingTexture((uint)Width, (uint)Height);
                            try
                            {
                                int initHr = _outputDuplication.AcquireNextFrame(500, out var initInfo, out var initResource);
                                if (initHr == S_OK && initInfo.AccumulatedFrames > 0)
                                {
                                    var texGuid = typeof(ID3D11Texture2D).GUID;
                                    initResource.QueryInterface(ref texGuid, out nint initTexPtr);
                                    initResource.Release();
                                    var initTex = (ID3D11Texture2D)Marshal.GetObjectForIUnknown(initTexPtr);
                                    _context!.CopyResource(_stagingTexture, initTex);
                                    initTex.Release();
                                    _outputDuplication.ReleaseFrame();
                                    _logger.LogDebug("DXGI staging texture pre-populated");
                                }
                            }
                            catch { /* Initial frame copy is best-effort */ }
                            
                            return; // Success
                        }
                        catch (Exception ex)
                        {
                            var comEx = ex as COMException;
                            int hresult = comEx?.HResult ?? -1;
                            _logger.LogWarning("DXGI output {Idx} DuplicateOutput failed: {Error} (HRESULT=0x{HR:X8})", outputIdx, ex.Message, hresult);
                        }
                        finally { Marshal.Release(outputPtr); }
                    }
                    catch { break; }
                }
            }
            finally { Marshal.ReleaseComObject(adapter); }
        }

        throw new InvalidOperationException("No adapter/output supports DXGI Desktop Duplication");
    }

    /// <summary>Capture a frame and return as BMP byte array. Returns null if no new frame.</summary>
    public byte[]? CaptureFrameBmp()
    {
        if (!IsAvailable || _outputDuplication == null) return null;

        try
        {
            int hr = _outputDuplication.AcquireNextFrame(16, out var frameInfo, out var desktopResource);
            if (hr == DXGI_ERROR_WAIT_TIMEOUT) return null;
            if (hr != S_OK) throw new COMException("AcquireNextFrame failed", hr);
            if (frameInfo.AccumulatedFrames == 0)
            {
                desktopResource.Release();
                _outputDuplication.ReleaseFrame();
                return null;
            }

            // Get texture
            var textureGuid = typeof(ID3D11Texture2D).GUID;
            desktopResource.QueryInterface(ref textureGuid, out nint texturePtr);
            desktopResource.Release();

            var texture = (ID3D11Texture2D)Marshal.GetObjectForIUnknown(texturePtr);
            texture.GetWidth(out uint texW);
            texture.GetHeight(out uint texH);

            // Create staging texture if needed
            if (_stagingTexture == null || Width != (int)texW || Height != (int)texH)
            {
                Width = (int)texW;
                Height = (int)texH;
                CreateStagingTexture(texW, texH);
            }

            // Copy GPU texture → staging → CPU
            _context!.CopyResource(_stagingTexture, texture);
            texture.Release();

            _context.Map(_stagingTexture, 0, D3D11_MAP_READ, 0, out var mapped);

            try
            {
                // Build BMP from BGRA pixels
                return BuildBmpFromBgra(mapped.pData, mapped.RowPitch, Width, Height);
            }
            finally
            {
                _context.Unmap(_stagingTexture, 0);
                _outputDuplication.ReleaseFrame();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("DXGI capture failed: {Error}", ex.Message);
            return null;
        }
    }


    // Reusable dirty rect buffer - zero alloc per frame
    private readonly byte[] _dirtyRectBuffer = new byte[256 * 16]; // max 256 dirty rects * sizeof(RECT)
    
    /// <summary>Smart DXGI capture: non-blocking, uses OS dirty rects. Returns null if no changes.</summary>
    public (uint Stride, int Width, int Height)? CaptureFrameBgraInto(byte[] target)
    {
        if (!IsAvailable || _outputDuplication == null || _context == null) return null;
        try
        {
            // Zero timeout = non-blocking, return immediately if no new frame
            int hr = _outputDuplication.AcquireNextFrame(0, out var frameInfo, out var desktopResource);
            
            if (hr == DXGI_ERROR_WAIT_TIMEOUT)
            {
                // No new frame from GPU = no screen changes
                return null;
            }
            
            if (hr != S_OK) throw new COMException("AcquireNextFrame failed", hr);
            
            if (frameInfo.AccumulatedFrames == 0)
            {
                desktopResource.Release();
                _outputDuplication.ReleaseFrame();
                return null; // No new frames accumulated
            }

            // Get dirty rects from OS (tells us what changed)
            int dirtyRectCount = 0;
            try
            {
                unsafe
                {
                    fixed (byte* pDirty = _dirtyRectBuffer)
                    {
                        _outputDuplication.GetFrameDirtyRects((uint)_dirtyRectBuffer.Length, (nint)pDirty, out uint dirtyRectsSize);
                        dirtyRectCount = (int)(dirtyRectsSize / 16); // each RECT is 16 bytes (4 ints)
                    }
                }
            }
            catch { dirtyRectCount = 1; } // If we can't get dirty rects, assume full screen changed
            
            if (dirtyRectCount == 0)
            {
                desktopResource.Release();
                _outputDuplication.ReleaseFrame();
                return null; // No changes detected by OS
            }

            // There ARE changes - copy the GPU frame to staging then to our buffer
            var textureGuid = typeof(ID3D11Texture2D).GUID;
            desktopResource.QueryInterface(ref textureGuid, out nint texturePtr);
            desktopResource.Release();

            var texture = (ID3D11Texture2D)Marshal.GetObjectForIUnknown(texturePtr);
            texture.GetWidth(out uint texW);
            texture.GetHeight(out uint texH);

            if (_stagingTexture == null || Width != (int)texW || Height != (int)texH)
            {
                Width = (int)texW;
                Height = (int)texH;
                CreateStagingTexture(texW, texH);
            }

            _context.CopyResource(_stagingTexture, texture);
            texture.Release();

            try { return ReadStagingTextureInto(target); }
            finally { _outputDuplication.ReleaseFrame(); }
        }
        catch { return null; }
    }

    private (uint Stride, int Width, int Height)? ReadStagingTextureInto(byte[] target)
    {
        if (_stagingTexture == null || _context == null) return null;
        _context.Map(_stagingTexture, 0, D3D11_MAP_READ, 0, out var mapped);
        try
        {
            int srcStride = (int)mapped.RowPitch;
            int pixelBytes = srcStride * Height;
            if (target.Length < pixelBytes) return null;
            Marshal.Copy(mapped.pData, target, 0, pixelBytes);
            return ((uint)srcStride, Width, Height);
        }
        finally { _context.Unmap(_stagingTexture, 0); }
    }
    /// <summary>Capture raw BGRA pixels from DXGI. For delta pipeline use.</summary>
    public (byte[] Pixels, uint Stride, int Width, int Height)? CaptureFrameBgra()
    {
        if (!IsAvailable || _outputDuplication == null) return null;
        try
        {
            int hr = _outputDuplication.AcquireNextFrame(16, out var frameInfo, out var desktopResource);
            if (hr == DXGI_ERROR_WAIT_TIMEOUT)
            {
                // No new frame — read from cached staging texture if available
                if (_stagingTexture == null) return null;
                return ReadStagingTexture();
            }
            if (hr != S_OK) throw new COMException("AcquireNextFrame failed", hr);
            if (frameInfo.AccumulatedFrames == 0)
            {
                desktopResource.Release();
                _outputDuplication.ReleaseFrame();
                // No new content — read from cached staging texture
                if (_stagingTexture == null) return null;
                return ReadStagingTexture();
            }

            var textureGuid = typeof(ID3D11Texture2D).GUID;
            desktopResource.QueryInterface(ref textureGuid, out nint texturePtr);
            desktopResource.Release();

            var texture = (ID3D11Texture2D)Marshal.GetObjectForIUnknown(texturePtr);
            texture.GetWidth(out uint texW);
            texture.GetHeight(out uint texH);

            if (_stagingTexture == null || Width != (int)texW || Height != (int)texH)
            {
                Width = (int)texW;
                Height = (int)texH;
                CreateStagingTexture(texW, texH);
            }

            _context!.CopyResource(_stagingTexture, texture);
            texture.Release();

            try { return ReadStagingTexture(); }
            finally { _outputDuplication.ReleaseFrame(); }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("DXGI BGRA capture failed: {Error}", ex.Message);
            return null;
        }
    }

    private (byte[] Pixels, uint Stride, int Width, int Height)? ReadStagingTexture()
    {
        if (_stagingTexture == null || _context == null) return null;
        _context.Map(_stagingTexture, 0, D3D11_MAP_READ, 0, out var mapped);
        try
        {
            int srcStride = (int)mapped.RowPitch;
            int pixelBytes = srcStride * Height;
            var pixels = new byte[pixelBytes];
            Marshal.Copy(mapped.pData, pixels, 0, pixelBytes);
            return (pixels, mapped.RowPitch, Width, Height);
        }
        finally { _context.Unmap(_stagingTexture, 0); }
    }
    private static byte[] BuildBmpFromBgra(nint pData, uint rowPitch, int width, int height)
    {
        // Output 24-bit BMP (3 bytes per pixel, row-aligned to 4 bytes)
        int bmpRowSize = ((width * 3 + 3) / 4) * 4;
        int pixelDataSize = bmpRowSize * height;
        int fileSize = 14 + 40 + pixelDataSize;
        var bmp = new byte[fileSize];

        // BMP header
        bmp[0] = (byte)'B'; bmp[1] = (byte)'M';
        bmp[2] = (byte)fileSize; bmp[3] = (byte)(fileSize >> 8); bmp[4] = (byte)(fileSize >> 16); bmp[5] = (byte)(fileSize >> 24);
        bmp[10] = 54; // data offset
        bmp[14] = 40; // DIB header size
        bmp[18] = (byte)width; bmp[19] = (byte)(width >> 8); bmp[20] = (byte)(width >> 16); bmp[21] = (byte)(width >> 24);
        var negH = -height; // top-down BMP
        bmp[22] = (byte)negH; bmp[23] = (byte)(negH >> 8); bmp[24] = (byte)(negH >> 16); bmp[25] = (byte)(negH >> 24);
        bmp[26] = 1; bmp[28] = 24; // planes=1, bpp=24

        // Convert BGRA → BGR (drop alpha, handle stride difference)
        unsafe
        {
            byte* src = (byte*)pData;
            fixed (byte* dst = &bmp[54])
            {
                for (int y = 0; y < height; y++)
                {
                    byte* srcRow = src + y * rowPitch;
                    byte* dstRow = dst + y * bmpRowSize;
                    for (int x = 0; x < width; x++)
                    {
                        dstRow[x * 3]     = srcRow[x * 4];     // B
                        dstRow[x * 3 + 1] = srcRow[x * 4 + 1]; // G
                        dstRow[x * 3 + 2] = srcRow[x * 4 + 2]; // R
                    }
                }
            }
        }
        return bmp;
    }

    private void CreateStagingTexture(uint width, uint height)
    {
        var desc = new D3D11_TEXTURE2D_DESC
        {
            Width = width, Height = height, MipLevels = 1, ArraySize = 1,
            Format = DXGI_FORMAT_B8G8R8A8_UNORM,
            SampleDesc = new DXGI_SAMPLE_DESC { Count = 1, Quality = 0 },
            Usage = D3D11_USAGE_STAGING, BindFlags = 0,
            CPUAccessFlags = D3D11_CPU_ACCESS_READ, MiscFlags = 0
        };
        _device!.CreateTexture2D(ref desc, nint.Zero, out _stagingTexture);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stagingTexture = null;
        _outputDuplication?.Release(); _outputDuplication = null;
        _context = null; _device = null;
        if (_d3dDevice != nint.Zero) { Marshal.Release(_d3dDevice); _d3dDevice = nint.Zero; }
        if (_dxgiFactory != nint.Zero) { Marshal.Release(_dxgiFactory); _dxgiFactory = nint.Zero; }
    }
}

public class Win32InputSimulator : IInputSimulator
{
    private readonly ILogger _logger;
    public Win32InputSimulator(ILogger logger) => _logger = logger;

    [DllImport("user32.dll")] private static extern void keybd_event(byte vk, byte scan, uint flags, nuint extra);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, int dx, int dy, uint data, nuint extra);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT lpPoint);
    [DllImport("user32.dll")] private static extern ushort VkKeyScan(char ch);
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }

    public Task TypeTextAsync(string text, CancellationToken ct = default)
    {
        foreach (var ch in text)
        {
            var vk = VkKeyScan(ch); var key = (byte)(vk & 0xFF); var shift = (vk & 0x100) != 0;
            if (shift) keybd_event(0x10, 0, 0, nuint.Zero);
            keybd_event(key, 0, 0, nuint.Zero); keybd_event(key, 0, 2, nuint.Zero);
            if (shift) keybd_event(0x10, 0, 2, nuint.Zero);
        }
        return Task.CompletedTask;
    }
    public Task PressKeyAsync(VirtualKey key, CancellationToken ct = default) { var vk = MapKey(key); keybd_event((byte)vk, 0, 0, nuint.Zero); keybd_event((byte)vk, 0, 2, nuint.Zero); return Task.CompletedTask; }
    public Task KeyDownAsync(VirtualKey key, CancellationToken ct = default) { keybd_event((byte)MapKey(key), 0, 0, nuint.Zero); return Task.CompletedTask; }
    public Task KeyUpAsync(VirtualKey key, CancellationToken ct = default) { keybd_event((byte)MapKey(key), 0, 2, nuint.Zero); return Task.CompletedTask; }
    public Task MoveMouseAsync(int x, int y, CancellationToken ct = default) { SetCursorPos(x, y); return Task.CompletedTask; }
    public async Task ClickAsync(int x, int y, MouseButton button = MouseButton.Left, CancellationToken ct = default) { await MoveMouseAsync(x, y, ct); var flag = button == MouseButton.Left ? 0x0002u : button == MouseButton.Right ? 0x0008u : 0x0020u; mouse_event(flag, 0, 0, 0, nuint.Zero); mouse_event(flag + 1, 0, 0, 0, nuint.Zero); }
    public async Task DoubleClickAsync(int x, int y, CancellationToken ct = default) { await ClickAsync(x, y, MouseButton.Left, ct); await ClickAsync(x, y, MouseButton.Left, ct); }
    public async Task DragAsync(int fromX, int fromY, int toX, int toY, CancellationToken ct = default) { await MoveMouseAsync(fromX, fromY, ct); mouse_event(0x0002, 0, 0, 0, nuint.Zero); await Task.Delay(50, ct); await MoveMouseAsync(toX, toY, ct); mouse_event(0x0004, 0, 0, 0, nuint.Zero); }
    public Task ScrollAsync(int deltaX, int deltaY, CancellationToken ct = default) { mouse_event(0x0800, 0, 0, (uint)deltaY, nuint.Zero); return Task.CompletedTask; }
    public Task<(int X, int Y)> GetMousePositionAsync() { GetCursorPos(out var pt); return Task.FromResult((pt.X, pt.Y)); }

    private static int MapKey(VirtualKey key) => key switch
    {
        VirtualKey.Enter => 0x0D, VirtualKey.Escape => 0x1B, VirtualKey.Tab => 0x09,
        VirtualKey.Space => 0x20, VirtualKey.Backspace => 0x08, VirtualKey.Delete => 0x2E,
        VirtualKey.Up => 0x26, VirtualKey.Down => 0x28, VirtualKey.Left => 0x25, VirtualKey.Right => 0x27,
        VirtualKey.Shift => 0x10, VirtualKey.Control => 0x11, VirtualKey.Alt => 0x12,
        VirtualKey.A => 0x41, VirtualKey.B => 0x42, VirtualKey.C => 0x43, VirtualKey.D => 0x44,
        VirtualKey.E => 0x45, VirtualKey.F => 0x46, VirtualKey.G => 0x47, VirtualKey.H => 0x48,
        VirtualKey.I => 0x49, VirtualKey.J => 0x4A, VirtualKey.K => 0x4B, VirtualKey.L => 0x4C,
        VirtualKey.M => 0x4D, VirtualKey.N => 0x4E, VirtualKey.O => 0x4F, VirtualKey.P => 0x50,
        VirtualKey.Q => 0x51, VirtualKey.R => 0x52, VirtualKey.S => 0x53, VirtualKey.T => 0x54,
        VirtualKey.U => 0x55, VirtualKey.V => 0x56, VirtualKey.W => 0x57, VirtualKey.X => 0x58,
        VirtualKey.Y => 0x59, VirtualKey.Z => 0x5A,
        VirtualKey.D0 => 0x30, VirtualKey.D1 => 0x31, VirtualKey.D2 => 0x32, VirtualKey.D3 => 0x33,
        VirtualKey.D4 => 0x34, VirtualKey.D5 => 0x35, VirtualKey.D6 => 0x36, VirtualKey.D7 => 0x37,
        VirtualKey.D8 => 0x38, VirtualKey.D9 => 0x39,
        VirtualKey.F1 => 0x70, VirtualKey.F2 => 0x71, VirtualKey.F3 => 0x72, VirtualKey.F4 => 0x73,
        VirtualKey.F5 => 0x74, VirtualKey.F6 => 0x75, VirtualKey.F7 => 0x76, VirtualKey.F8 => 0x77,
        VirtualKey.F9 => 0x78, VirtualKey.F10 => 0x79, VirtualKey.F11 => 0x7A, VirtualKey.F12 => 0x7B,
        VirtualKey.Home => 0x24, VirtualKey.End => 0x23, VirtualKey.PageUp => 0x21, VirtualKey.PageDown => 0x22,
        VirtualKey.Insert => 0x2D, VirtualKey.Pause => 0x13, VirtualKey.CapsLock => 0x14,
        VirtualKey.NumLock => 0x90, VirtualKey.ScrollLock => 0x91, VirtualKey.PrintScreen => 0x2C,
        VirtualKey.Meta => 0x5B, _ => 0
    };
}

public class Win32ClipboardManager : IClipboardManager
{
    private readonly ILogger _logger;
    public Win32ClipboardManager(ILogger logger) => _logger = logger;

    [DllImport("user32.dll")] private static extern bool OpenClipboard(nint hWnd);
    [DllImport("user32.dll")] private static extern bool CloseClipboard();
    [DllImport("user32.dll")] private static extern nint GetClipboardData(uint format);
    [DllImport("user32.dll")] private static extern bool EmptyClipboard();
    [DllImport("user32.dll")] private static extern nint SetClipboardData(uint format, nint hMem);
    [DllImport("kernel32.dll")] private static extern nint GlobalAlloc(uint flags, nint size);
    [DllImport("kernel32.dll")] private static extern nint GlobalLock(nint hMem);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(nint hMem);
    [DllImport("kernel32.dll")] private static extern nint GlobalFree(nint hMem);
    private const uint CF_UNICODETEXT = 13;
    private const uint GMEM_MOVEABLE = 0x0002;

    public Task<string> GetTextAsync(CancellationToken ct = default)
    {
        if (!OpenClipboard(nint.Zero)) return Task.FromResult("");
        try
        {
            var handle = GetClipboardData(CF_UNICODETEXT);
            if (handle == nint.Zero) return Task.FromResult("");
            var ptr = GlobalLock(handle);
            if (ptr == nint.Zero) return Task.FromResult("");
            try { return Task.FromResult(Marshal.PtrToStringUni(ptr) ?? ""); }
            finally { GlobalUnlock(handle); }
        }
        finally { CloseClipboard(); }
    }

    public Task SetTextAsync(string text, CancellationToken ct = default)
    {
        if (!OpenClipboard(nint.Zero)) return Task.CompletedTask;
        try
        {
            EmptyClipboard();
            var bytes = Encoding.Unicode.GetBytes(text + '\0');
            var hMem = GlobalAlloc(GMEM_MOVEABLE, bytes.Length);
            if (hMem == nint.Zero) return Task.CompletedTask;
            var ptr = GlobalLock(hMem);
            if (ptr == nint.Zero) { GlobalFree(hMem); return Task.CompletedTask; }
            Marshal.Copy(bytes, 0, ptr, bytes.Length);
            GlobalUnlock(hMem);
            SetClipboardData(CF_UNICODETEXT, hMem);
        }
        finally { CloseClipboard(); }
        return Task.CompletedTask;
    }
}

public class Win32WindowManager : IWindowManager
{
    private readonly ILogger _logger;
    public Win32WindowManager(ILogger logger) => _logger = logger;

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc proc, nint lParam);
    [DllImport("user32.dll")] private static extern int GetWindowTextLength(nint hWnd);
    [DllImport("user32.dll")] private static extern int GetWindowText(nint hWnd, StringBuilder sb, int maxCount);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hWnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint hWnd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hWnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint hWnd, int cmd);
    [DllImport("user32.dll")] private static extern bool PostMessage(nint hWnd, uint msg, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hWnd, out uint processId);

    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool QueryFullProcessImageName(nint hProcess, uint flags, StringBuilder lpExeName, ref uint lpdwSize);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);

    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const int SW_RESTORE = 9;
    private const uint WM_CLOSE = 0x0010;
    private delegate bool EnumWindowsProc(nint hWnd, nint lParam);
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }

    private static string GetProcessNameByPid(uint pid)
    {
        var hProc = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (hProc == nint.Zero) return "";
        try
        {
            var sb = new StringBuilder(260);
            uint size = 260;
            if (!QueryFullProcessImageName(hProc, 0, sb, ref size)) return "";
            var name = sb.ToString();
            var lastSlash = name.LastIndexOfAny(['\\', '/']);
            var fileName = lastSlash >= 0 ? name[(lastSlash + 1)..] : name;
            if (fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                fileName = fileName[..^4];
            return fileName;
        }
        finally { CloseHandle(hProc); }
    }

    public Task<WindowInfo[]> ListWindowsAsync(CancellationToken ct = default)
    {
        var windows = new List<WindowInfo>();
        EnumWindows((hWnd, _) =>
        {
            if (!IsWindowVisible(hWnd)) return true;
            var len = GetWindowTextLength(hWnd);
            if (len == 0) return true;
            var sb = new StringBuilder(len + 1);
            GetWindowText(hWnd, sb, sb.Capacity);
            GetWindowRect(hWnd, out var rect);
            GetWindowThreadProcessId(hWnd, out var pid);
            var procName = GetProcessNameByPid(pid);
            windows.Add(new WindowInfo(hWnd, sb.ToString(), procName, rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top, true, IsIconic(hWnd)));
            return true;
        }, nint.Zero);
        return Task.FromResult(windows.ToArray());
    }

    public Task FocusWindowAsync(nint handle, CancellationToken ct = default)
    {
        if (IsIconic(handle)) ShowWindow(handle, SW_RESTORE);
        SetForegroundWindow(handle);
        return Task.CompletedTask;
    }

    public Task CloseWindowAsync(nint handle, CancellationToken ct = default)
    {
        PostMessage(handle, WM_CLOSE, nint.Zero, nint.Zero);
        return Task.CompletedTask;
    }

    public Task<(int X, int Y, int Width, int Height)> GetWindowBoundsAsync(nint handle)
    {
        GetWindowRect(handle, out var rect);
        return Task.FromResult((rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top));
    }
}