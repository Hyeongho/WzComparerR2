using System.Runtime.InteropServices;
using WzComparerR2.Common;
using WzComparerR2.PluginBase;
using WzComparerR2.WzLib;

namespace WzNativeLib;

public static unsafe partial class WzExports
{
	// 기존 배치 구조체와 export의 ABI는 바꾸지 않고 별도 API로 확장한다.
	private static readonly Dictionary<ulong, NativeSpineScene> s_SpineScenes = new();
	private static ulong s_NextSpineId = 1;
	[ThreadStatic] private static string? s_SpineError;

	[UnmanagedCallersOnly(EntryPoint = "wz_spine_api_version")]
	public static int WzSpineApiVersion()
	{
		return 1;
	}

	[UnmanagedCallersOnly(EntryPoint = "wz_spine_last_error")]
	public static IntPtr WzSpineLastError()
	{
		return Marshal.StringToCoTaskMemUTF8(s_SpineError ?? "");
	}

	// layer=-1이면 back, 0~7이면 해당 레이어의 obj 슬롯이다. 실제 spineAni/timeScale을 배치에서 읽는다.
	[UnmanagedCallersOnly(EntryPoint = "wz_spine_load_placement")]
	public static ulong WzSpineLoadPlacement(IntPtr wzPath, IntPtr mapPath, int layer, int index, NativeSpineMeta* outMeta)
	{
		if (outMeta == null)
		{
			return 0;
		}
		*outMeta = default;
		s_SpineError = null;
		try
		{
			if (layer < -1 || layer > 7)
			{
				throw new ArgumentOutOfRangeException(nameof(layer));
			}
			Wz_Node? map = FindMapNode(wzPath, mapPath);
			Wz_Node? placement = layer == -1 ? map?.Nodes["back"]?.Nodes[index.ToString()]
				: map?.Nodes[layer.ToString()]?.Nodes["obj"]?.Nodes[index.ToString()];
			if (placement == null)
			{
				throw new InvalidDataException("Map placement not found.");
			}
			string resourcePath;
			if (layer == -1)
			{
				// spineNo가 없는 경우 폴더명은 spine이다. spine0으로 강제하면 최신 맵을 찾지 못한다.
				int? spineNo = placement.Nodes["spineNo"].GetValueEx<int>();
				resourcePath = $@"Map\Back\{placement.Nodes["bS"].GetValueEx("")}.img\spine{spineNo}\{placement.Nodes["no"].GetValueEx<string>("")}";
			}
			else
			{
				resourcePath = $@"Map\Obj\{placement.Nodes["oS"].GetValueEx("")}.img\{placement.Nodes["l0"].GetValueEx("")}\{placement.Nodes["l1"].GetValueEx("")}\{placement.Nodes["l2"].GetValueEx("")}";
			}
			Wz_Node? resource = PluginManager.FindWz(resourcePath)?.ResolveUol();
			SpineDetectionResult detection = NativeSpineScene.Detect(resource);
			if (!detection.Success || resource == null)
			{
				throw new InvalidDataException($"{resourcePath}: {detection.ErrorDetail}");
			}
			NativeSpineScene scene = new(resource, detection,
				placement.Nodes["spineAni"].GetValueEx<string>(null), placement.Nodes["timeScale"].GetValueEx(100) / 100.0f);
			try
			{
				ulong id = s_NextSpineId++;
				s_SpineScenes.Add(id, scene);
				*outMeta = scene.Meta;
				return id;
			}
			catch
			{
				scene.Dispose();
				throw;
			}
		}
		catch (Exception error)
		{
			s_SpineError = error.Message;
			return 0;
		}
	}

	[UnmanagedCallersOnly(EntryPoint = "wz_spine_read_page")]
	public static IntPtr WzSpineReadPage(ulong id, int index, int* width, int* height, int* byteCount)
	{
		if (width == null || height == null || byteCount == null)
		{
			return IntPtr.Zero;
		}
		*width = *height = *byteCount = 0;
		IntPtr pixels = IntPtr.Zero;
		try
		{
			NativeSpineScene.Page page = s_SpineScenes[id].Pages[index];
			pixels = Marshal.AllocCoTaskMem(page.Pixels.Length);
			Marshal.Copy(page.Pixels, 0, pixels, page.Pixels.Length);
			*width = page.Width; *height = page.Height; *byteCount = page.Pixels.Length;
			return pixels;
		}
		catch (Exception error)
		{
			if (pixels != IntPtr.Zero)
			{
				Marshal.FreeCoTaskMem(pixels);
			}
			s_SpineError = error.Message;
			return IntPtr.Zero;
		}
	}

	// 성공 시 1, 빈 포즈도 성공이다. outVertices는 wz_free로 해제한다.
	[UnmanagedCallersOnly(EntryPoint = "wz_spine_update")]
	public static int WzSpineUpdate(ulong id, float deltaTime, IntPtr* outVertices, int* outCount)
	{
		if (outVertices == null || outCount == null)
		{
			return 0;
		}
		*outVertices = IntPtr.Zero;
		*outCount = 0;
		try
		{
			NativeSpineScene scene = s_SpineScenes[id];
			scene.Update(deltaTime);
			*outVertices = AllocArray(scene.Vertices, outCount);
			return 1;
		}
		catch (Exception error)
		{
			s_SpineError = error.Message;
			return 0;
		}
	}

	[UnmanagedCallersOnly(EntryPoint = "wz_spine_destroy")]
	public static void WzSpineDestroy(ulong id)
	{
		if (s_SpineScenes.Remove(id, out NativeSpineScene? scene))
		{
			scene.Dispose();
		}
	}
}
