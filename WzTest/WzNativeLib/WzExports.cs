// WzExports.cs — WzComparerR2.WzLib Native AOT 래퍼
//
// 빌드:
//   dotnet publish -r win-x64 -p:NativeLib=Shared -c Release
//   → bin\Release\net8.0\win-x64\publish\WzNativeLib.dll
//
// 반환 형식 (wz_open / wz_load_folder / wz_load_file, UTF-8):
//   첫 줄: "OK" 또는 "ERR\t<메시지>"
//   이후 각 줄: "<depth>\t<type>\t<name>"
//     depth : 0부터 시작하는 정수 (0 = 최상위 자식)
//     type  : 'I' (img) 또는 'D' (directory)
//     name  : 노드 이름

using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using WzComparerR2;
using WzComparerR2.WzLib;
using WzComparerR2.AvatarCommon;
using WzComparerR2.PluginBase;
using WzComparerR2.CharaSim;

namespace WzNativeLib;

public static unsafe class WzExports
{
	// ── wz_open ─────────────────────────────────────────────────────────────
	// MainForm.openWz() 동일 로직 — 파일 포맷을 자동 감지해서 로드
	//
	// pathUtf8: UTF-8 경로
	//   - *.ms / *.mn          → LoadMsFile
	//   - KMST1125 Base.wz     → LoadKMST1125DataWz (+ Packs/*.ms 자동 로드)
	//   - 그 외 *.wz           → Load(path, true)
	//
	// 반환: 트리 문자열 (wz_free 로 해제 필요)
	[UnmanagedCallersOnly(EntryPoint = "wz_open")]
	public static IntPtr WzOpen(IntPtr pathUtf8)
	{
		try
		{
			string path = Marshal.PtrToStringUTF8(pathUtf8)
						  ?? throw new ArgumentNullException("path");

			Wz_Node? rootNode = OpenWzPathAndGetRoot(path);

			var sb = new StringBuilder(65536);
			sb.AppendLine("OK");

			if (rootNode != null)
				TraverseNode(rootNode, -1, sb);

			return MarshalUtf8(sb.ToString());
		}
		catch (Exception ex)
		{
			return MarshalUtf8("ERR\t" + ex.Message.Replace('\n', ' '));
		}
	}

	// ── wz_load_folder ──────────────────────────────────────────────────────
	// folderPathUtf8: UTF-8 경로 (예: C:\Nexon\Maple\Data\Skill)
	// 반환: 트리 문자열 (wz_free 로 해제 필요)
	[UnmanagedCallersOnly(EntryPoint = "wz_load_folder")]
	public static IntPtr WzLoadFolder(IntPtr folderPathUtf8)
	{
		try
		{
			string path = Marshal.PtrToStringUTF8(folderPathUtf8)
						  ?? throw new ArgumentNullException("path");

			var sb = new StringBuilder(65536);
			sb.AppendLine("OK");

			var structure = new Wz_Structure();
			Wz_Node? rootNode = null;
			structure.LoadWzFolder(path, ref rootNode, false);

			if (rootNode != null)
				TraverseNode(rootNode, -1, sb);

			return MarshalUtf8(sb.ToString());
		}
		catch (Exception ex)
		{
			return MarshalUtf8("ERR\t" + ex.Message.Replace('\n', ' '));
		}
	}

	// ── wz_load_file ────────────────────────────────────────────────────────
	// filePathUtf8: UTF-8 경로 (예: C:\Nexon\Maple\Data\Base\Base.wz)
	// 반환: 트리 문자열 (wz_free 로 해제 필요)
	[UnmanagedCallersOnly(EntryPoint = "wz_load_file")]
	public static IntPtr WzLoadFile(IntPtr filePathUtf8)
	{
		try
		{
			string path = Marshal.PtrToStringUTF8(filePathUtf8)
						  ?? throw new ArgumentNullException("path");

			var sb = new StringBuilder(65536);
			sb.AppendLine("OK");

			var structure = new Wz_Structure();
			structure.Load(path, false);

			if (structure.WzNode != null)
				TraverseNode(structure.WzNode, -1, sb);

			return MarshalUtf8(sb.ToString());
		}
		catch (Exception ex)
		{
			return MarshalUtf8("ERR\t" + ex.Message.Replace('\n', ' '));
		}
	}

	// ── wz_read_img ─────────────────────────────────────────────────────────
	// wzPathUtf8 : WZ 파일 경로 (예: C:\Maple\Data\Skill.wz)
	//              또는 WZ 폴더 경로 (예: C:\Maple\Data\Skill)
	// imgPathUtf8: IMG 노드 이름 또는 백슬래시 구분 경로 (예: "Dragon.img", "Skill\Dragon.img")
	//              빈 문자열이면 wzPath 자체가 .img 파일로 간주
	// outLen     : 반환 바이트 수 (실패 시 0)
	// 반환       : IMG raw 바이트 포인터 (wz_free로 해제), 실패 시 IntPtr.Zero
	[UnmanagedCallersOnly(EntryPoint = "wz_read_img")]
	public static IntPtr WzReadImg(IntPtr wzPathUtf8, IntPtr imgPathUtf8, int* outLen)
	{
		*outLen = 0;
		try
		{
			string wzPath = Marshal.PtrToStringUTF8(wzPathUtf8) ?? throw new ArgumentNullException("wzPath");
			string imgPath = Marshal.PtrToStringUTF8(imgPathUtf8) ?? "";

			Wz_Image? img = null;

			if (string.IsNullOrEmpty(imgPath))
			{
				// wzPath 자체가 .img 파일인 경우
				var structure = new Wz_Structure();
				structure.Load(wzPath, false);
				img = structure.WzNode?.Value as Wz_Image;
			}
			else
			{
				// wzPath = WZ 파일 또는 폴더, imgPath = 내부 노드 경로 (백슬래시 구분)
				Wz_Node? root;
				if (Directory.Exists(wzPath))
				{
					var structure = new Wz_Structure();
					Wz_Node? rootNode = null;
					structure.LoadWzFolder(wzPath, ref rootNode, false);
					root = rootNode ?? structure.WzNode;
				}
				else
				{
					root = OpenWzPathAndGetRoot(wzPath);
				}

				// FindNodeByPath(string, bool) → 내부에서 '\' 로 분리 (Wz_Node.cs:121)
				Wz_Node? found = root?.FindNodeByPath(imgPath, false);
				img = found?.Value as Wz_Image;
			}

			if (img == null)
				return IntPtr.Zero;

			using var stream = img.OpenRead();
			int len = (int)stream.Length;
			byte[] bytes = new byte[len];
			int read = 0;
			while (read < len)
			{
				int n = stream.Read(bytes, read, len - read);
				if (n == 0) break;
				read += n;
			}

			IntPtr ptr = Marshal.AllocCoTaskMem(read);
			Marshal.Copy(bytes, 0, ptr, read);
			*outLen = read;
			return ptr;
		}
		catch
		{
			return IntPtr.Zero;
		}
	}

	// ── wz_read_canvas ──────────────────────────────────────────────────────
	// wzPathUtf8  : WZ 파일 경로 또는 WZ 폴더 경로 (wz_read_img와 동일)
	// nodePathUtf8: IMG 내부 Canvas 노드까지의 백슬래시 구분 경로
	//               (예: "Face.img\00020000\face\0")
	// outWidth/outHeight: 디코딩된 이미지 크기 (실패 시 0)
	// outLen      : 반환 바이트 수 = width*height*4 (실패 시 0)
	// 반환        : BGRA8888(메모리상 B,G,R,A 순서) 픽셀 바이트 포인터
	//               (wz_free로 해제), 실패 시 IntPtr.Zero
	[UnmanagedCallersOnly(EntryPoint = "wz_read_canvas")]
	public static IntPtr WzReadCanvas(IntPtr wzPathUtf8, IntPtr nodePathUtf8, int* outWidth, int* outHeight, int* outLen)
	{
		*outWidth = 0;
		*outHeight = 0;
		*outLen = 0;
		try
		{
			string wzPath = Marshal.PtrToStringUTF8(wzPathUtf8) ?? throw new ArgumentNullException("wzPath");
			string nodePath = Marshal.PtrToStringUTF8(nodePathUtf8) ?? throw new ArgumentNullException("nodePath");

			Wz_Node? root;
			if (Directory.Exists(wzPath))
			{
				var structure = new Wz_Structure();
				Wz_Node? rootNode = null;
				structure.LoadWzFolder(wzPath, ref rootNode, false);
				root = rootNode ?? structure.WzNode;
			}
			else
			{
				root = OpenWzPathAndGetRoot(wzPath);
			}

			// extractImage: true — IMG 내부까지 자동으로 파고들며 프로퍼티를 추출한다.
			Wz_Node? found = root?.FindNodeByPath(nodePath, true);

			if (found?.Value is not Wz_Png png)
				return IntPtr.Zero;

			using Bitmap bmp = png.ExtractPng();
			int width = bmp.Width;
			int height = bmp.Height;
			var rect = new Rectangle(0, 0, width, height);
			BitmapData data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
			try
			{
				int rowBytes = width * 4;
				int totalBytes = rowBytes * height;
				IntPtr ptr = Marshal.AllocCoTaskMem(totalBytes);

				// Stride가 rowBytes와 다를 수 있으므로(정렬 패딩) 줄 단위로 복사한다.
				for (int y = 0; y < height; y++)
				{
					IntPtr srcRow = IntPtr.Add(data.Scan0, y * data.Stride);
					IntPtr dstRow = IntPtr.Add(ptr, y * rowBytes);
					Buffer.MemoryCopy((void*)srcRow, (void*)dstRow, rowBytes, rowBytes);
				}

				*outWidth = width;
				*outHeight = height;
				*outLen = totalBytes;
				return ptr;
			}
			finally
			{
				bmp.UnlockBits(data);
			}
		}
		catch
		{
			return IntPtr.Zero;
		}
	}

	// ── wz_read_avatar ──────────────────────────────────────────────────────
	// wzPathUtf8      : WZ 파일 경로 또는 WZ 폴더 경로 (Base.wz 등, wz_read_canvas와
	//                   동일하게 KMST 병합/레거시 분할 포맷 자동 처리)
	// loadoutSpecUtf8 : 콤마 구분 29칸 위치 기반 아이템 ID 목록 — WzComparerR2
	//                   GUI(AvatarForm.GetAllPartsTag())가 만드는 "아바타 코드"와
	//                   동일한 포맷이라 그대로 재사용한다. AvatarCanvas.Parts[]
	//                   인덱스 순서 그대로:
	//                   0=Body,1=Head,2=Face,3=Hair,4=Cap,5=Coat,6=Longcoat,
	//                   7=Pants,8=Shoes,9=Glove,10=SubWeapon,11=Cape,12=Weapon,
	//                   13=Earrings,14=FaceAccessory,15=EyeAccessory,16=Taming,
	//                   17=Saddle,18=Chair,19=Effect,20=Pendant,21=Belt,
	//                   22=ShoulderPad,23=Pocket,24=Emblem,25=Ring1,26=Ring2,
	//                   27=Ring3,28=Ring4. 장착 안 한 슬롯은 빈 칸으로 둔다
	//                   (예: "2015,12015,53003,65007,,,1054087,,1073816,,,,1703431,,,,,,,,,,,,,,,,").
	// actionNameUtf8/frameIndex        : 몸 액션 이름("stand1" 등)과 프레임 번호
	// emotionNameUtf8/emotionFrameIndex: 표정 이름("default" 등)과 프레임 번호
	// outWidth/outHeight     : 합성된 이미지 크기 (실패 시 0)
	// outOriginX/outOriginY  : 합성 결과의 그리기 기준점 —
	//                          AvatarCanvas.DrawFrame()의 -rect.X/-rect.Y
	// outLen                 : 반환 바이트 수 = width*height*4 (실패 시 0)
	// outDelayMs             : 이 frameIndex를 화면에 표시할 시간(ms) —
	//                          AvatarCanvas.GetActionFrames(actionName)의
	//                          frameIndex번째 ActionFrame.AbsoluteDelay
	//                          (WZ "delay" 프로퍼티, 노드가 없으면 120ms
	//                          기본값 — LoadActionFrameDesc과 동일한 폴백).
	//                          찾지 못하면(frameIndex가 액션 프레임 개수를
	//                          벗어남 등) 120 그대로 유지.
	// 반환                   : BGRA8888 픽셀 바이트 포인터(wz_free로 해제),
	//                          실패 시 IntPtr.Zero
	[UnmanagedCallersOnly(EntryPoint = "wz_read_avatar")]
	public static IntPtr WzReadAvatar(
		IntPtr wzPathUtf8, IntPtr loadoutSpecUtf8,
		IntPtr actionNameUtf8, int frameIndex,
		IntPtr emotionNameUtf8, int emotionFrameIndex,
		int* outWidth, int* outHeight, int* outOriginX, int* outOriginY, int* outLen, int* outDelayMs)
	{
		*outWidth = 0;
		*outHeight = 0;
		*outOriginX = 0;
		*outOriginY = 0;
		*outLen = 0;
		*outDelayMs = 120;
		try
		{
			string wzPath = Marshal.PtrToStringUTF8(wzPathUtf8) ?? throw new ArgumentNullException("wzPath");
			string loadoutSpec = Marshal.PtrToStringUTF8(loadoutSpecUtf8) ?? "";
			string actionName = Marshal.PtrToStringUTF8(actionNameUtf8) ?? "stand1";
			string emotionName = Marshal.PtrToStringUTF8(emotionNameUtf8) ?? "default";

			Wz_Node? root;
			if (Directory.Exists(wzPath))
			{
				var structure = new Wz_Structure();
				Wz_Node? rootNode = null;
				structure.LoadWzFolder(wzPath, ref rootNode, false);
				root = rootNode ?? structure.WzNode;
			}
			else
			{
				root = OpenWzPathAndGetRoot(wzPath);
			}

			if (root == null)
				return IntPtr.Zero;

			// AvatarCommon 소스 내부의 PluginManager.FindWz 호출들(LoadActions/
			// LoadEmotions/AvatarPart의 아이콘 로딩 등)이 이 루트를 쓰도록 연결.
			// (PluginManagerShim.cs — 실제 GUI PluginBase 프로젝트는 링크하지 않음.)
			PluginManager.CurrentRoot = root;

			var canvas = new AvatarCanvas();
			// extractImage: true 필수 — false(기본값)면 Wz_Image.TryExtract()가
			// 호출되지 않아 zmap.img의 자식 노드(z-순서 이름 목록)가 하나도 안
			// 채워진 빈 노드가 반환된다. AvatarCanvas.LoadZ(Wz_Node)는 노드
			// 자체가 null이 아니면(빈 노드라도) true를 반환하므로 이 실패가
			// 겉으로 티가 안 나고, this.ZMap이 조용히 빈 리스트로 남는다 —
			// GenerateLayer()가 문자열 Z를 ZMap.IndexOf()로 찾다가 전부 못 찾아
			// (모두 -1 → ZMap.Count(=0) 또는 "default"만 0) 사실상 모든
			// 문자열 z 레이어가 같은 ZIndex로 뭉개져서 정렬이 원래 삽입 순서에
			// 가깝게 무너진다 — 무기가 팔 앞/뒤 중 틀린 쪽에 그려지는 등
			// 파츠별 레이어 순서가 깨지는 증상의 원인. Body/Head/Face/Hair
			// 파츠 로딩에서 이미 한 번 겪었던 것과 같은 종류의 버그(아래 참고,
			// "wz_read_avatar 런타임 실패 수정 — extractImage 누락" 커밋)인데
			// zmap.img 쪽은 그때 놓쳤다.
			canvas.LoadZ(root.FindNodeByPath(@"Base\zmap.img", true));
			canvas.LoadActions();
			canvas.LoadEmotions();

			Wz_Node? characterRoot = root.FindNodeByPath("Character");

			string[] slots = loadoutSpec.Split(',');
			for (int i = 0; i < slots.Length; i++)
			{
				if (!int.TryParse(slots[i].Trim(), out int id))
					continue;

				switch (i)
				{
					case 0: // Body — Parts[0]의 ID는 "Character\{id:D8}.img" 파일명에서 그대로 파싱된 값이라
							// (skin % 2000) + 2000 같은 공식 없이 바로 경로를 재구성할 수 있다.
					case 1: // Head — 마찬가지로 저장된 값 자체가 "Character\{id:D8}.img"의 그 8자리다.
					{
						// extractImage: true 필수 — false(기본값)면 .img 경계에서
						// Wz_Image.TryExtract()가 호출되지 않아 내부 트리(map/z/액션
						// 프레임 등)가 하나도 안 채워진 빈 노드가 반환된다.
						Wz_Node? node = root.FindNodeByPath($@"Character\{id:D8}.img", true);
						if (node != null) canvas.AddPart(node);
						break;
					}
					case 2: // Face
					{
						Wz_Node? node = root.FindNodeByPath($@"Character\Face\{id:D8}.img", true);
						if (node != null)
						{
							canvas.AddPart(node);
							canvas.LoadEmotions(); // 얼굴 파츠에 맞는 표정 목록을 다시 로드
						}
						break;
					}
					case 3: // Hair
					{
						Wz_Node? node = root.FindNodeByPath($@"Character\Hair\{id:D8}.img", true);
						if (node != null) canvas.AddPart(node);
						break;
					}
					default:
					{
						// 나머지 장비 슬롯(Cap~Ring4, 인덱스 4~28) — 정확한 하위 폴더명을
						// 전부 확신할 수 없어서(예: SubWeapon/Earrings/FaceAccessory가
						// 실제 WZ 폴더명과 정확히 일치하는지 미검증), 기존처럼
						// Character.wz 하위를 재귀 탐색해서 {id:D8}.img를 찾는다
						// (AvatarCanvasManager.FindNodeByGearID와 동일한 방식, _Canvas
						// 폴더는 건너뜀) — 어느 슬롯인지는 AvatarCanvas.AddPart 내부의
						// Gear.GetGearType()이 아이템 ID로 알아서 판별한다.
						Wz_Node? gearNode = FindGearNode(characterRoot, id);
						if (gearNode != null)
						{
							canvas.AddPart(gearNode);

							// 무기(인덱스 12)가 캐시(젤) 무기(GearType.cashWeapon,
							// ID/10000==170)면 일반 장비와 다른 렌더링 경로를 탄다 —
							// AvatarCanvas.CreateFrame이 canvas.WeaponType 값으로
							// 무기 .img 안의 실제 무기 타입 서브폴더("130","137" 등)를
							// 골라서 그리는데, 기본값(0)인 채로 두면 그 서브폴더가
							// 없어서 빈 프레임이 만들어지고 조용히 버려진다(예외/로그
							// 없음) — 무기가 "찾기는 했는데 안 그려지는" 지금 증상의
							// 원인. 실제 착용 무기의 원래 타입 정보가 없으므로, GUI
							// 앱의 기본 선택 로직과 동일하게 이 무기가 가진 유효한
							// 타입 목록 중 첫 번째를 그대로 쓴다 — 총(WeaponMotionType
							// ==3) 전용 특수 케이스는 원본 모션 타입 정보가 없어 범위
							// 밖으로 둔다.
							if (i == 12 && Gear.GetGearType(id) == GearType.cashWeapon)
							{
								var weaponTypes = canvas.GetCashWeaponTypes();
								if (weaponTypes.Count > 0)
									canvas.WeaponType = weaponTypes[0];
							}
						}
						break;
					}
				}
			}

			canvas.ActionName = actionName;
			canvas.EmotionName = emotionName;

			// 프레임별 표시 시간(ms) — GetActionFrames()는 CreateFrame()과 별개로
			// action.Name 기준 프레임 목록을 다시 훑어서 각 프레임의 "delay" WZ
			// 프로퍼티(LoadActionFrameDesc, AvatarCanvas.cs:919)를 채워 돌려준다.
			// CreateFrame()이 내부적으로 쓰는 ActionFrame은 이 메서드 밖으로 안
			// 나오므로, 딜레이만 별도로 조회한다 — canvas.LoadActions()가 이미
			// 위에서 호출됐으므로 this.Actions에 actionName이 등록돼 있어야 함.
			ActionFrame[] actionFrames = canvas.GetActionFrames(actionName);
			if (frameIndex >= 0 && frameIndex < actionFrames.Length)
			{
				*outDelayMs = actionFrames[frameIndex].AbsoluteDelay;
			}

			// 무기 자체에 내장된 기본 이펙트(총구 화염, 찌르기 잔상 등 — 무기 타입
			// 서브폴더 안 액션/프레임 폴더에서 "weapon" png와 나란히 있는 "effect"
			// png, AvatarCanvas.cs:1417)는 ShowWeaponEffect/ShowWeaponJumpEffect가
			// 켜져 있어야 CreateBone이 실제로 그린다(꺼져 있으면 continue로 건너뜀).
			// 라이브러리 기본값이 이미 true(AvatarCanvas.cs:35-36)이긴 하지만, 항상
			// 켜져 있음을 코드로 보장하기 위해 명시적으로 설정한다. (해당 액션에
			// 실제 effect 데이터가 없으면 — 예: stand1 — 여전히 아무것도 안
			// 그려지는 게 정상이다, 이건 게이트가 아니라 데이터 유무 문제.)
			canvas.ShowWeaponEffect = true;
			canvas.ShowWeaponJumpEffect = true;

			// effectFrames — 무기/케이프/반지 등 일부 아이템은 자체 이미지 말고도
			// "Effect/ItemEff.img/{ID}/effect"에 별도로 붙는 부가 이펙트(예: 무기
			// 발광 효과)를 갖고 있다(AvatarPart.cs:92, LoadInfo 시 자동으로 EffectNode에
			// 채워짐). CreateFrame이 이 레이어를 실제로 그리려면 effectFrames가
			// null이 아니라 "각 레이어에서 몇 번째 프레임을 쓸지" 배열이어야 하는데
			// (AvatarCanvas.cs:1060 — null이면 이 블록 전체를 건너뛰어 모든 아이템
			// 이펙트가 무조건 빠짐), 지금까지 null을 넘겨서 이펙트가 있는 아이템도
			// 항상 이펙트 없이 렌더링되고 있었다. 모든 레이어에 0번 프레임을
			// 요청해두면(LayerSlotLength = 29(Parts) + 4(체어/이펙트 2단 레이어)),
			// 실제로 이펙트 데이터가 없는 파츠는 GetEffectFrame/LinkEffectParts가
			// null을 그대로 반환해 안전하게 스킵되고(AvatarCanvas.cs:895-898,
			// 2039), 이펙트가 있는 파츠만 첫 프레임이 자동으로 덧그려진다 — 프레임
			// 애니메이션 재생은 이번 범위 밖이라 항상 0번 고정.
			int[] effectFrames = new int[AvatarCanvas.LayerSlotLength];
			Bone bone = canvas.CreateFrame(frameIndex, emotionFrameIndex, 0, effectFrames);
			if (bone == null)
				return IntPtr.Zero;

			BitmapOrigin bitmapOrigin = canvas.DrawFrame(bone);
			if (bitmapOrigin.Bitmap == null)
				return IntPtr.Zero;

			using Bitmap bmp = bitmapOrigin.Bitmap;
			int width = bmp.Width;
			int height = bmp.Height;
			var rect = new Rectangle(0, 0, width, height);
			BitmapData data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
			try
			{
				int rowBytes = width * 4;
				int totalBytes = rowBytes * height;
				IntPtr ptr = Marshal.AllocCoTaskMem(totalBytes);

				for (int y = 0; y < height; y++)
				{
					IntPtr srcRow = IntPtr.Add(data.Scan0, y * data.Stride);
					IntPtr dstRow = IntPtr.Add(ptr, y * rowBytes);
					Buffer.MemoryCopy((void*)srcRow, (void*)dstRow, rowBytes, rowBytes);
				}

				*outWidth = width;
				*outHeight = height;
				*outOriginX = bitmapOrigin.Origin.X;
				*outOriginY = bitmapOrigin.Origin.Y;
				*outLen = totalBytes;
				return ptr;
			}
			finally
			{
				bmp.UnlockBits(data);
			}
		}
		catch
		{
			return IntPtr.Zero;
		}
	}

	// Character.wz 하위(카테고리 폴더 한 단계 + 그 안쪽 한 단계, _Canvas 폴더는
	// 건너뜀)에서 "{id:D8}.img" 이름을 재귀 탐색한다.
	// AvatarCanvasManager.FindNodeByGearID와 동일한 탐색 방식.
	//
	// 이름만 매칭하고 끝내면 안 된다 — 여기서 순회하는 node1.Nodes는 아직
	// 한 번도 열리지 않은 .img 노드들이라 내부 트리가 비어있다(FindNodeByPath의
	// extractImage=true와 동일하게, 매칭된 .img는 직접 TryExtract()를 호출해서
	// 실제 내용을 채워야 AvatarCanvas.AddPart가 map/z/아이콘 데이터를 찾을 수 있다).
	private static Wz_Node? FindGearNode(Wz_Node? characterRoot, int id)
	{
		if (characterRoot == null)
			return null;

		string imgName = id.ToString("D8") + ".img";

		foreach (var node1 in characterRoot.Nodes)
		{
			if (node1.Text.Contains("_Canvas"))
				continue;

			if (node1.Text == imgName)
				return ExtractImgNode(node1);

			foreach (var node2 in node1.Nodes)
			{
				if (node2.Text == imgName)
					return ExtractImgNode(node2);
			}
		}

		return null;
	}

	// Wz_Node.FindNodeByPath(path, extractImage: true) 내부 구현과 동일한 패턴 —
	// 매칭된 .img 노드를 TryExtract()로 직접 열어서 실제 내용이 채워진 노드를 반환한다.
	private static Wz_Node ExtractImgNode(Wz_Node node)
	{
		var img = node.GetValue<Wz_Image>();
		if (img != null && img.TryExtract())
			return img.Node;
		return node;
	}

	// ── wz_free ─────────────────────────────────────────────────────────────
	// wz_open / wz_load_folder / wz_load_file / wz_read_img 가 반환한 포인터를 해제
	[UnmanagedCallersOnly(EntryPoint = "wz_free")]
	public static void WzFree(IntPtr ptr)
	{
		if (ptr != IntPtr.Zero)
			Marshal.FreeCoTaskMem(ptr);
	}

	// ── 내부 헬퍼 ────────────────────────────────────────────────────────────

	// wz_open의 파일 형식 자동 감지 로직(원래 여기에만 있었음)을 wz_read_img /
	// wz_read_canvas도 공유하도록 추출한 헬퍼. 이게 없으면 저 두 함수는
	// Base.wz(레거시 분할 구조든, KMST1125 Packs 병합 구조든)를 못 연다.
	//   - *.ms / *.mn          → LoadMsFile
	//   - KMST1125 Base.wz     → LoadKMST1125DataWz (+ Packs/*.ms 자동 로드)
	//   - 그 외 *.wz(레거시 Base.wz 분할 구조 포함) → Load(path, useBaseWz: true)
	private static Wz_Node? OpenWzPathAndGetRoot(string path)
	{
		string ext = Path.GetExtension(path);

		if (string.Equals(ext, ".ms", StringComparison.OrdinalIgnoreCase) ||
			string.Equals(ext, ".mn", StringComparison.OrdinalIgnoreCase))
		{
			var wz = new Wz_Structure();
			wz.LoadMsFile(path);
			return wz.WzNode;
		}

		var structure = new Wz_Structure();

		if (structure.IsKMST1125WzFormat(path))
		{
			structure.LoadKMST1125DataWz(path);

			// Base.wz 옆 Packs 폴더의 .ms/.mn 파일도 로드 (MainForm.openWz 동일)
			if (string.Equals(Path.GetFileName(path), "Base.wz", StringComparison.OrdinalIgnoreCase))
			{
				string? dataDir = Path.GetDirectoryName(Path.GetDirectoryName(path));
				if (dataDir != null)
				{
					string packsDir = Path.Combine(dataDir, "Packs");
					if (Directory.Exists(packsDir))
					{
						foreach (string msFile in Directory.GetFiles(packsDir, "*.ms")
							.Concat(Directory.GetFiles(packsDir, "*.mn")))
						{
							structure.LoadMsFile(msFile);
						}
					}
				}
			}

			return structure.WzNode;
		}

		// useBaseWz: true — 레거시 Base.wz 분할 파일 구조(디렉터리 항목이
		// 다른 .wz 파일을 참조)도 올바르게 해석한다.
		structure.Load(path, true);
		return structure.WzNode;
	}

	private static void TraverseNode(Wz_Node node, int depth, StringBuilder sb)
	{
		if (depth >= 0)
		{
			char type = node.Value is Wz_Image ? 'I' : 'D';
			sb.Append(depth);
			sb.Append('\t');
			sb.Append(type);
			sb.Append('\t');
			sb.AppendLine(node.Text);
		}

		foreach (Wz_Node child in node.Nodes)
			TraverseNode(child, depth + 1, sb);
	}

	private static IntPtr MarshalUtf8(string s)
	{
		byte[] bytes = Encoding.UTF8.GetBytes(s);
		IntPtr ptr = Marshal.AllocCoTaskMem(bytes.Length + 1);
		Marshal.Copy(bytes, 0, ptr, bytes.Length);
		Marshal.WriteByte(ptr, bytes.Length, 0);
		return ptr;
	}
}