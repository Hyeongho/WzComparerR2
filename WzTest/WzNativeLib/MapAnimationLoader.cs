// MapAnimationLoader.cs — WzComparerR2.MapRender의 ResourceLoader 해석 규칙을
// 헤드리스(Native AOT) 환경으로 손포팅한 것.
//
// 왜 링크가 아니라 포팅인가:
//   ResourceLoader.cs는 MonoGame의 Texture2D/TextureAtlas를 반환하고 SpineLoader를
//   부르기 때문에 WzNativeLib(Native AOT, GUI 의존 없음)에 그대로 링크할 수 없다.
//   반면 "WZ 노드 → 프레임 목록 + 프레임별 메타데이터"를 결정하는 규칙 자체는
//   순수 로직이라 이 파일로 옮겨올 수 있다. AvatarCommon을 소스 단위로 링크한
//   것과 같은 접근(WzNativeLib.csproj 주석 참고)이되, 이 파일은 우리가 직접
//   작성한 것이라 csproj의 기본 <Compile> 규칙으로 잡힌다.
//
// 대응 관계:
//   EnumerateFrames  ← ResourceLoader.InnerLoadAnimationData (ResourceLoader.cs:327)
//   ReadFrame        ← ResourceLoader.LoadFrame             (ResourceLoader.cs:442)
//
// 반드시 지켜야 하는 규칙(레퍼런스와 어긋나면 그림이 틀어짐):
//   1) 프레임 노드는 UOL 체인을 먼저 푼다.
//   2) PNG는 GetLinkedSourceNode(source/_inlink/_outlink)가 가리키는 "링크 대상"
//      노드에서 가져오지만, z/delay/blend/origin/lt/rb/a0/a1 메타데이터는
//      "원본" 노드에서 읽는다. 이 둘을 같은 노드에서 읽으면 링크된 타일·
//      오브젝트의 원점과 z가 전부 틀어진다.
//   3) delay 기본값은 120ms (MapRender V1의 TextureLoader는 100이지만 V2는 120).
//   4) a1의 기본값은 255가 아니라 a0.

using System.Drawing;
using System.Drawing.Imaging;
using WzComparerR2.Common;
using WzComparerR2.PluginBase;
using WzComparerR2.WzLib;

namespace WzNativeLib;

// 한 프레임의 메타데이터 — 픽셀은 별도의 큰 blob에 모아두고 PixelOffset으로 가리킨다.
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, Pack = 4)]
public struct NativeAnimFrame
{
	public int PixelOffset; // blob 시작으로부터의 바이트 오프셋
	public int Width, Height;
	public int OriginX, OriginY;
	public int Z;       // 컨테이너 내 정렬 1차 키(MeshItem.Z0)
	public int DelayMs; // 기본 120
	public int A0, A1;  // 프레임 구간 시작/끝 알파 (A1 기본값 = A0)
	public int Blend;   // 0 아니면 가산 블렌딩
	public int LtX, LtY, RbX, RbY;
}

[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, Pack = 4)]
public struct NativeAnimMeta
{
	public int FrameCount;
	public int Repeat;
	public int IsSpine; // 1이면 spine 애니메이션 — 이번 범위 밖이라 호출자가 건너뛴다
	public int HasFlowX, FlowX;
	public int HasFlowY, FlowY;
	// 전체 프레임 (-origin, size) 사각형의 합집합 — back의 cx/cy 기본값과
	// 타일링 컬링에 필요(FrameAnimationData.GetBound 대응).
	public int BoundsX, BoundsY, BoundsW, BoundsH;
	public int PixelBytes; // blob 전체 크기
}

internal sealed class LoadedFrame
{
	public NativeAnimFrame Meta;
	public byte[]? Pixels; // BGRA8888
}

internal static class MapAnimationLoader
{
	// ResourceLoader.InnerLoadAnimationData 대응.
	// 애니메이션 노드(= 경로로 찾아낸 노드) 아래에서 실제 프레임 노드들을 찾아낸다.
	// 반환 null = spine이거나 프레임이 없음.
	public static List<Wz_Node>? EnumerateFrames(Wz_Node? aniNode, out bool isSpine, out bool repeat)
	{
		isSpine = false;
		repeat = false;

		aniNode = aniNode?.ResolveUol();
		if (aniNode == null)
			return null;

		// 단일 캔버스 — 프레임 하나짜리 애니메이션으로 취급한다.
		if (aniNode.Value is Wz_Png)
		{
			return new List<Wz_Node> { aniNode };
		}

		// spine 판별: 레퍼런스는 SpineLoader.Detect를 쓰지만 그건 spine-monogame에
		// 의존한다. 여기서는 spine 데이터가 갖는 특징 노드만 확인해서 "우리가 못
		// 그리는 것"으로만 분류하면 충분하다(그리지 않고 건너뛰는 게 전부라서).
		if (aniNode.Nodes["skeleton"] != null || aniNode.Nodes["atlas"] != null)
		{
			isSpine = true;
			return null;
		}

		// 레퍼런스(ResourceLoader.cs:392)는 `node.Nodes["repeat"].GetValueEx<bool>()`
		// (인자 없는 nullable 오버로드)로 읽어서 `Data.Repeat ?? true`로 소비한다 —
		// 즉 "repeat" 프로퍼티 자체가 없으면 기본값은 "반복(loop)"이고, 명시적으로
		// repeat=0인 경우에만 "한 번 재생하고 마지막 프레임에서 정지"가 된다.
		// 예전 코드는 `GetValueEx(0)`(기본값 있는 오버로드)를 써서 프로퍼티가 없을
		// 때 0(=정지)으로 기본값이 뒤집혀 있었다 — PickFrame이 repeat을 아예 안 보던
		// 동안은 죽은 코드라 드러나지 않았지만, PickFrame이 repeat을 보게 고친 지금은
		// 이 기본값이 반대로 되어 있으면 "repeat 프로퍼티가 없는" 흔한 케이스까지
		// 전부 한 번만 재생하고 멈추는 쪽으로 뒤집혀 버린다.
		int? repeatValue = aniNode.Nodes["repeat"].GetValueEx<int>();
		repeat = repeatValue.HasValue ? repeatValue.Value != 0 : true;

		var frames = new List<Wz_Node>();
		Wz_Node? first = aniNode.Nodes["0"];

		// 레퍼런스 ResourceLoader.cs:380-384의 우회 — "0"이 있는데 값이 비어 있으면
		// 한 단계 더 안쪽이 실제 프레임 목록인 경우가 있다(예: back/0/0).
		if (first != null && first.Value == null && first.Nodes["0"] != null)
		{
			aniNode = first;
		}

		for (int i = 0; ; i++)
		{
			Wz_Node? frameNode = aniNode.Nodes[i.ToString()];
			if (frameNode == null)
				break;

			frames.Add(frameNode);
		}

		return frames.Count > 0 ? frames : null;
	}

	// ResourceLoader.LoadFrame 대응.
	// node = 프레임 노드(원본). 픽셀은 링크 대상에서, 메타는 원본에서 읽는다.
	public static LoadedFrame? ReadFrame(Wz_Node? node)
	{
		if (node == null)
			return null;

		// 1) UOL 체인 해제
		while (node?.Value is Wz_Uol uol)
		{
			node = uol.HandleUol(node);
		}

		if (node == null)
			return null;

		// 2) source / _inlink / _outlink 해석 — PNG는 이쪽에서 가져온다.
		//    (PluginManager.FindWz는 PluginManagerShim이 CurrentRoot 기준으로 구현,
		//     WzExports.GetRoot가 매 진입 시 세팅해준다.)
		Wz_Node? linkNode = node.GetLinkedSourceNode(PluginManager.FindWz);
		if (linkNode?.Value is not Wz_Png png)
			return null;

		var result = new LoadedFrame();

		// 3) 메타데이터는 전부 "원본" node에서 — 링크 대상이 아니다.
		Wz_Vector? origin = node.Nodes["origin"].GetValueEx<Wz_Vector>(null);
		if (origin != null)
		{
			result.Meta.OriginX = origin.X;
			result.Meta.OriginY = origin.Y;
		}

		result.Meta.Z = node.Nodes["z"].GetValueEx(0);
		result.Meta.DelayMs = node.Nodes["delay"].GetValueEx(120);
		result.Meta.Blend = node.Nodes["blend"].GetValueEx(0) != 0 ? 1 : 0;
		result.Meta.A0 = node.Nodes["a0"].GetValueEx(255);
		result.Meta.A1 = node.Nodes["a1"].GetValueEx(result.Meta.A0);

		Wz_Vector? lt = node.Nodes["lt"].GetValueEx<Wz_Vector>(null);
		if (lt != null)
		{
			result.Meta.LtX = lt.X;
			result.Meta.LtY = lt.Y;
		}

		Wz_Vector? rb = node.Nodes["rb"].GetValueEx<Wz_Vector>(null);
		if (rb != null)
		{
			result.Meta.RbX = rb.X;
			result.Meta.RbY = rb.Y;
		}

		// 4) 픽셀 디코딩 (BGRA8888 — wz_read_canvas와 동일 포맷)
		using Bitmap bmp = png.ExtractPng();
		if (bmp == null)
			return null;

		int width = bmp.Width;
		int height = bmp.Height;
		int rowBytes = width * 4;
		byte[] pixels = new byte[rowBytes * height];

		BitmapData data = bmp.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
		try
		{
			// Stride가 rowBytes보다 클 수 있으므로(정렬 패딩) 줄 단위로 복사.
			for (int y = 0; y < height; y++)
			{
				IntPtr srcRow = IntPtr.Add(data.Scan0, y * data.Stride);
				System.Runtime.InteropServices.Marshal.Copy(srcRow, pixels, y * rowBytes, rowBytes);
			}
		}
		finally
		{
			bmp.UnlockBits(data);
		}

		result.Meta.Width = width;
		result.Meta.Height = height;
		result.Pixels = pixels;
		return result;
	}

	// 애니메이션 노드 하나를 통째로 로드한다. 프레임이 하나도 안 나오면 null.
	public static List<LoadedFrame>? LoadAll(Wz_Node? aniNode, out NativeAnimMeta meta)
	{
		meta = default;

		List<Wz_Node>? frameNodes = EnumerateFrames(aniNode, out bool isSpine, out bool repeat);
		if (isSpine)
		{
			meta.IsSpine = 1;
			return null;
		}
		if (frameNodes == null)
			return null;

		var loaded = new List<LoadedFrame>();
		foreach (Wz_Node frameNode in frameNodes)
		{
			LoadedFrame? frame = ReadFrame(frameNode);
			if (frame != null)
				loaded.Add(frame);
		}

		if (loaded.Count == 0)
			return null;

		meta.FrameCount = loaded.Count;
		meta.Repeat = repeat ? 1 : 0;

		// flowX/flowY는 맵 노드가 아니라 애니메이션 노드에 붙어 있다
		// (MapData.cs:760-780). 스크롤 back에서만 쓰인다.
		Wz_Node? resolved = aniNode?.ResolveUol();
		int? flowX = resolved?.Nodes["flowX"].GetValueEx<int>();
		int? flowY = resolved?.Nodes["flowY"].GetValueEx<int>();
		if (flowX != null) { meta.HasFlowX = 1; meta.FlowX = flowX.Value; }
		if (flowY != null) { meta.HasFlowY = 1; meta.FlowY = flowY.Value; }

		// Bounds = 각 프레임 (-origin, size) 사각형의 합집합 (GetBound 대응).
		int left = int.MaxValue, top = int.MaxValue, right = int.MinValue, bottom = int.MinValue;
		int pixelBytes = 0;
		foreach (LoadedFrame f in loaded)
		{
			int fl = -f.Meta.OriginX;
			int ft = -f.Meta.OriginY;
			left = Math.Min(left, fl);
			top = Math.Min(top, ft);
			right = Math.Max(right, fl + f.Meta.Width);
			bottom = Math.Max(bottom, ft + f.Meta.Height);

			f.Meta.PixelOffset = pixelBytes;
			pixelBytes += f.Pixels!.Length;
		}

		meta.BoundsX = left;
		meta.BoundsY = top;
		meta.BoundsW = right - left;
		meta.BoundsH = bottom - top;
		meta.PixelBytes = pixelBytes;

		return loaded;
	}
}
