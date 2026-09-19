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
				Wz_Node? root = GetRoot(wzPath);

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
	// outOriginX/outOriginY: 이 캔버스 노드의 형제 "origin" 프로퍼티(Wz_Vector,
	//               없으면 0,0) — 타일/오브젝트/back 전부 발밑·기준점 정렬에
	//               필요해서 추가. wz_read_avatar는 아바타 합성 결과 자체의
	//               별도 원점(AvatarCanvas.DrawFrame이 계산)이라 이 값과는 무관.
	// outDelayMs  : 이 캔버스 노드의 형제 "delay" 프로퍼티(ms, 없으면 120) —
	//               AvatarCanvas.LoadActionFrameDesc와 동일한 "Nodes["delay"]
	//               읽고 없으면 120" 패턴. 애니메이션이 아닌 단일 프레임
	//               캔버스도 그냥 120이 채워지며, 호출자가 프레임이 1개뿐이면
	//               무시하면 된다.
	// outLen      : 반환 바이트 수 = width*height*4 (실패 시 0)
	// 반환        : BGRA8888(메모리상 B,G,R,A 순서) 픽셀 바이트 포인터
	//               (wz_free로 해제), 실패 시 IntPtr.Zero
	[UnmanagedCallersOnly(EntryPoint = "wz_read_canvas")]
	public static IntPtr WzReadCanvas(IntPtr wzPathUtf8, IntPtr nodePathUtf8, int* outWidth, int* outHeight, int* outOriginX, int* outOriginY, int* outDelayMs, int* outLen)
	{
		*outWidth = 0;
		*outHeight = 0;
		*outOriginX = 0;
		*outOriginY = 0;
		*outDelayMs = 120;
		*outLen = 0;
		try
		{
			string wzPath = Marshal.PtrToStringUTF8(wzPathUtf8) ?? throw new ArgumentNullException("wzPath");
			string nodePath = Marshal.PtrToStringUTF8(nodePathUtf8) ?? throw new ArgumentNullException("nodePath");

			Wz_Node? root = GetRoot(wzPath);

			// extractImage: true — IMG 내부까지 자동으로 파고들며 프로퍼티를 추출한다.
			Wz_Node? found = root?.FindNodeByPath(nodePath, true);

			if (found?.Value is not Wz_Png png)
				return IntPtr.Zero;

			Wz_Vector? origin = found.Nodes["origin"].GetValueEx<Wz_Vector>(null);
			if (origin != null)
			{
				*outOriginX = origin.X;
				*outOriginY = origin.Y;
			}
			*outDelayMs = found.Nodes["delay"].GetValueEx<int>(120);

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

			// GetRoot()가 PluginManager.CurrentRoot까지 세팅해준다 — AvatarCommon
			// 소스 내부의 PluginManager.FindWz 호출들(LoadActions/LoadEmotions/
			// AvatarPart의 아이콘 로딩 등)이 이 루트를 쓴다.
			// (PluginManagerShim.cs — 실제 GUI PluginBase 프로젝트는 링크하지 않음.)
			Wz_Node? root = GetRoot(wzPath);

			if (root == null)
				return IntPtr.Zero;

			var canvas = new AvatarCanvas();
			// extractImage: true 필수 — false(기본값)면 Wz_Image.TryExtract()가
			// 호출되지 않아 zmap.img의 자식 노드(z-순서 이름 목록)가 하나도 안
			// 채워진 빈 노드가 반환된다.
			//
			// "Base\zmap.img" 경로 자체가 이 브리지 구조에서는 틀렸을 수 있다 —
			// 실제 WzComparerR2 GUI의 PluginManager.FindWz("Base\\...")는
			// "Base"를 트리 안 폴더가 아니라 개별 .wz 파일이 등록된 레지스트리
			// 키로 취급하는데, 우리 PluginManagerShim은 경로 전체를 CurrentRoot
			// 하나의 트리 안 폴더 경로로 취급한다(PluginManagerShim.cs). 이전
			// 라운드에 extractImage=true만 추가하고 "Base\" 접두사는 그대로
			// 뒀는데도 무기 진단 로그가 zMapCount=0을 계속 보고해서, 이번엔
			// 접두사 자체가 이 WZ 구조(KMST1125류 병합 클라이언트는 zmap.img가
			// 원본 파일명 계층 없이 루트에 바로 있을 수 있음)와 안 맞을 가능성을
			// 추가로 의심 — 실패하면 접두사 없이 재시도한다.
			Wz_Node zMapNode = root.FindNodeByPath(@"Base\zmap.img", true);
			string zMapPathTried = @"Base\zmap.img";
			if (zMapNode == null)
			{
				zMapNode = root.FindNodeByPath(@"zmap.img", true);
				zMapPathTried = @"zmap.img (Base\ 접두사 실패 후 폴백)";
			}
			bool zLoaded = canvas.LoadZ(zMapNode);

			// 진단용 — 어느 경로로 찾았는지, 못 찾았는지/찾았는데 자식이
			// 0개인지 구분한다. AvatarCanvas.LoadZ(Wz_Node)는 노드 자체가
			// null이 아니면(빈 노드라도) true를 반환하므로 zMapCount=0만
			// 봐서는 "경로가 틀림"과 "찾긴 했는데 데이터가 비어있음"을
			// 구분할 수 없었다.
			try
			{
				string logPath = Path.Combine(AppContext.BaseDirectory, "wz_avatar_debug.log");
				File.AppendAllText(logPath,
					$"zmap: triedPath={zMapPathTried} nodeFound={(zMapNode != null)} childCount={(zMapNode?.Nodes.Count ?? -1)} LoadZ={zLoaded} ZMap.Count={canvas.ZMap.Count}{Environment.NewLine}");
			}
			catch
			{
			}

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

			// 진단용 — 무기 파츠의 Skin.Z/ZIndex가 프레임마다 실제로 어떻게
			// 나오는지 파일로 남긴다. AvatarCanvas.cs는 건드리지 않고(계속
			// 벤더 코드 취급), CreateFrame()이 이미 만들어준 Bone 트리를
			// 우리가 읽기만 한다. 실패해도 아바타 렌더링 자체에는 영향 없도록
			// 전체를 try-catch로 감싼다 — 파츠별 레이어 순서가 프레임에 따라
			// 안 바뀐다는 리포트의 원인(코드 버그 vs 데이터 자체가 프레임 간
			// 무변화)을 가르기 위한 임시 진단.
			try
			{
				string logPath = Path.Combine(AppContext.BaseDirectory, "wz_avatar_debug.log");
				void WalkSkins(Bone b)
				{
					foreach (var skin in b.Skins)
					{
						if (skin.Name != null && skin.Name.StartsWith("weapon"))
						{
							int resolvedIndex = string.IsNullOrEmpty(skin.Z) ? int.MinValue : canvas.ZMap.IndexOf(skin.Z);
							File.AppendAllText(logPath,
								$"action={actionName} frame={frameIndex} skin={skin.Name} Z={skin.Z ?? "(null)"} ZIndex={skin.ZIndex} resolvedZMapIndex={resolvedIndex} zMapCount={canvas.ZMap.Count}{Environment.NewLine}");
						}
					}
					foreach (var child in b.Children)
					{
						WalkSkins(child);
					}
				}
				WalkSkins(bone);
			}
			catch
			{
				// 진단 실패는 무시 — 렌더링 경로에 영향 주면 안 됨.
			}

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

	// ── Map.wz 구조 데이터 export ───────────────────────────────────────────
	// 픽셀은 여기서 안 돌려준다 — 각 아이템의 필드로 캔버스 노드 경로를
	// 계산해서(예: obj는 "Map\Obj\{oS}.img\{l0}\{l1}\{l2}\{frame}" — 이건
	// WzComparerR2.MapRender/Patches2/ObjItem.cs:131에서 실제로 그렇게
	// 만드는 걸 코드로 확인함) 기존 wz_read_canvas를 프레임 인덱스
	// 0,1,2...로 반복 호출하면 된다(wz_read_avatar의 walk1 프레임 로딩과
	// 동일 패턴). tile("Tile\{tS}.img\{u}\{no}")과 back
	// ("Map\Back\{bS}.img\back|ani\{no}[\frame]", ani=0/1로 분기)은
	// MapleStory WZ의 표준 관례이긴 하지만 이 저장소 코드에서 직접 확인한
	// obj 경로만큼 확실친 않음 — 실제로 돌려서 안 맞으면 C++ 쪽 경로
	// 조합 한 줄만 고치면 됨(구조 데이터 자체는 원본 WZ 필드 그대로라
	// 안 바뀜).
	//
	// 문자열 필드(oS/tS/u/bS 등)는 고정 32바이트 UTF-8 버퍼 — 실제 WZ
	// 식별자는 전부 훨씬 짧아서(예: "acc6","minar","nature2","bsc","enH1")
	// 32면 충분하다고 보지만, 넘치면 WriteFixedUtf8이 자르고 항상
	// null-terminate한다(버퍼 오버런 없음). 배열은 전부 wz_free로 해제.

	[StructLayout(LayoutKind.Sequential, Pack = 4)]
	public struct NativeMapInfo
	{
		public int VRLeft, VRTop, VRRight, VRBottom;
	}

	// Index(= WZ 슬롯 번호, 노드 이름을 정수로 파싱한 값)는 정렬 2차 키로 쓰인다.
	// MapRender의 MeshItem.Z1이 바로 이 값이며(FrmMapRender2.SceneRendering.cs의
	// GetMeshBack/Tile/Obj), 배열에 담긴 순서가 아니라 WZ가 매긴 번호여야 한다.
	[StructLayout(LayoutKind.Sequential, Pack = 4)]
	public unsafe struct NativeMapBackItem
	{
		public fixed byte Bs[32];
		public int Index;
		public int No;
		public int Ani;   // 0=정적("back"), 1=애니메이션("ani"), 2=spine(미지원)
		public int SpineNo;
		public int Front; // 0=맵 레이어보다 뒤, 1=맵 레이어보다 앞
		public int F;     // 좌우 반전
		public int X, Y;
		public int Rx, Ry; // 카메라 이동 비율(파라랙스) — 0이면 화면 고정, -100이면 월드 고정
		public int Type;   // TileMode: 1=가로,2=세로,3=가로+세로,4=가로스크롤,5=세로스크롤…
		public int Cx, Cy; // 타일링 셀 크기(0이면 프레임 Bounds 크기로 대체)
		public int A;      // 알파(0~255)
		public int W;      // 0이 아니면 Wx/Wy를 스크롤 거리(distance)로 사용
		public int Wx, Wy; // 스크롤 거리(W가 0이거나 값이 0이면 기본 100)
		public int ScreenMode;
	}

	[StructLayout(LayoutKind.Sequential, Pack = 4)]
	public unsafe struct NativeMapTileItem
	{
		public fixed byte U[32]; // 타일 조각 이름(예: "bsc","enH1")
		public int Index;
		public int No;
		public int X, Y;
		// zM은 MapRender2가 정렬에 쓰지 않는다(레거시 FrmMapRender에만 남아있고
		// 거기서도 "not use for sort" 주석이 달려 있음) — 원본 보존용으로만 둔다.
		public int Zm;
	}

	[StructLayout(LayoutKind.Sequential, Pack = 4)]
	public unsafe struct NativeMapObjItem
	{
		public fixed byte Os[32];
		public fixed byte L0[32];
		public fixed byte L1[32];
		public fixed byte L2[32];
		public int Index;
		public int X, Y, Z, Zm, F;
	}

	[StructLayout(LayoutKind.Sequential, Pack = 4)]
	public struct NativeFootholdItem
	{
		// id/layer/group을 반드시 같이 보존한다 — prev/next는 "같은
		// layer·group 안의 다른 id"를 가리키는 값이라 이것들이 없으면
		// 의미가 없어진다(계층 정보 유실). 물리 연동은 이번 범위 밖 —
		// 파싱 단계에서는 수직 선분도 거르지 않고 원본 그래프 그대로 둔다.
		public int Id;
		public int Layer;
		public int Group;
		public int X1, Y1, X2, Y2;
		public int Prev, Next, Piece;
	}

	// dest를 항상 0으로 채운 뒤(=항상 null-terminated) value를 UTF-8로
	// 최대 destSize-1바이트까지만 복사한다 — 넘치는 부분은 자르되 절대
	// destSize를 벗어나 쓰지 않는다.
	private static void WriteFixedUtf8(byte* dest, int destSize, string? value)
	{
		for (int i = 0; i < destSize; i++)
			dest[i] = 0;

		if (string.IsNullOrEmpty(value) || destSize <= 0)
			return;

		byte[] bytes = Encoding.UTF8.GetBytes(value);
		int copyLen = Math.Min(bytes.Length, destSize - 1);
		for (int i = 0; i < copyLen; i++)
			dest[i] = bytes[i];
	}

	// items를 하나의 네이티브 배열로 묶어 할당(wz_free로 해제). 빈 배열이면
	// outCount=0, IntPtr.Zero.
	private static IntPtr AllocArray<T>(List<T> items, int* outCount) where T : unmanaged
	{
		*outCount = items.Count;
		if (items.Count == 0)
			return IntPtr.Zero;

		int size = sizeof(T);
		IntPtr ptr = Marshal.AllocCoTaskMem(size * items.Count);
		for (int i = 0; i < items.Count; i++)
		{
			*(T*)(ptr + i * size) = items[i];
		}
		return ptr;
	}

	// wzPathUtf8/mapPathUtf8으로 맵 .img 노드를 연다. mapPathUtf8은 WZ 루트
	// 기준 백슬래시 경로(예: "Map\Map\Map2\240020210.img"). extractImage:
	// true 필수 — 안 그러면 back/foothold/레이어 전부 빈 노드로 돌아온다
	// (아바타 로딩 때 zmap.img에서 이미 겪은 것과 같은 클래스의 문제).
	private static Wz_Node? FindMapNode(IntPtr wzPathUtf8, IntPtr mapPathUtf8)
	{
		string wzPath = Marshal.PtrToStringUTF8(wzPathUtf8) ?? throw new ArgumentNullException("wzPath");
		string mapPath = Marshal.PtrToStringUTF8(mapPathUtf8) ?? throw new ArgumentNullException("mapPath");

		Wz_Node? root = GetRoot(wzPath);

		return root?.FindNodeByPath(mapPath, true);
	}

	// ── wz_map_read_info ────────────────────────────────────────────────────
	// outInfo: 호출자가 들고 있는 NativeMapInfo에 그대로 채워 넣는다(별도
	// 힙 할당/해제 없음 — 단일 고정 크기 구조체라 배열 패턴을 안 씀).
	// 반환: 성공하면 1, info 노드가 없거나 실패하면 0(outInfo는 0으로 채워짐).
	[UnmanagedCallersOnly(EntryPoint = "wz_map_read_info")]
	public static int WzMapReadInfo(IntPtr wzPathUtf8, IntPtr mapPathUtf8, NativeMapInfo* outInfo)
	{
		*outInfo = default;
		try
		{
			Wz_Node? infoNode = FindMapNode(wzPathUtf8, mapPathUtf8)?.Nodes["info"];
			if (infoNode == null)
				return 0;

			outInfo->VRLeft = infoNode.Nodes["VRLeft"].GetValueEx(0);
			outInfo->VRTop = infoNode.Nodes["VRTop"].GetValueEx(0);
			outInfo->VRRight = infoNode.Nodes["VRRight"].GetValueEx(0);
			outInfo->VRBottom = infoNode.Nodes["VRBottom"].GetValueEx(0);
			return 1;
		}
		catch
		{
			return 0;
		}
	}

	// ── wz_map_read_back ────────────────────────────────────────────────────
	// outCount: 아이템 개수(실패/없음 시 0)
	// 반환: NativeMapBackItem 배열(wz_free로 해제), 실패 시 IntPtr.Zero
	[UnmanagedCallersOnly(EntryPoint = "wz_map_read_back")]
	public static IntPtr WzMapReadBack(IntPtr wzPathUtf8, IntPtr mapPathUtf8, int* outCount)
	{
		*outCount = 0;
		try
		{
			Wz_Node? backNode = FindMapNode(wzPathUtf8, mapPathUtf8)?.Nodes["back"];
			if (backNode == null)
				return IntPtr.Zero;

			var items = new List<NativeMapBackItem>();
			foreach (Wz_Node node in backNode.Nodes)
			{
				NativeMapBackItem item = default;
				WriteFixedUtf8(item.Bs, 32, node.Nodes["bS"].GetValueEx<string>(null));
				item.Index = int.TryParse(node.Text, out int backIndex) ? backIndex : 0;
				item.No = node.Nodes["no"].GetValueEx(0);
				item.Ani = node.Nodes["ani"].GetValueEx(0);
				item.SpineNo = node.Nodes["spineNo"].GetValueEx(0);
				item.Front = node.Nodes["front"].GetValueEx(0);
				item.F = node.Nodes["f"].GetValueEx(0);
				item.X = node.Nodes["x"].GetValueEx(0);
				item.Y = node.Nodes["y"].GetValueEx(0);
				item.Rx = node.Nodes["rx"].GetValueEx(0);
				item.Ry = node.Nodes["ry"].GetValueEx(0);
				item.Type = node.Nodes["type"].GetValueEx(0);
				item.Cx = node.Nodes["cx"].GetValueEx(0);
				item.Cy = node.Nodes["cy"].GetValueEx(0);
				item.A = node.Nodes["a"].GetValueEx(255);
				item.W = node.Nodes["w"].GetValueEx(0);
				item.Wx = node.Nodes["wx"].GetValueEx(0);
				item.Wy = node.Nodes["wy"].GetValueEx(0);
				item.ScreenMode = node.Nodes["screenMode"].GetValueEx(0);
				items.Add(item);
			}

			return AllocArray(items, outCount);
		}
		catch
		{
			*outCount = 0;
			return IntPtr.Zero;
		}
	}

	// ── wz_map_read_layer ───────────────────────────────────────────────────
	// layerIndex: 0~7 (맵 .img 바로 아래 숫자 키 노드)
	// outTs/tsBufferSize: 레이어의 info\tS(타일셋 이름) — 고정 버퍼, 호출자가
	//                     할당해서 넘김(배열이 아니라 이것도 별도 힙 없음)
	// outTsMag  : info\tSMag(없으면 1)
	// outTiles/outTileCount, outObjs/outObjCount: 각각 wz_free로 해제
	// 반환: 레이어 노드 자체가 없으면 0, 있으면(내용이 비어 있어도) 1
	[UnmanagedCallersOnly(EntryPoint = "wz_map_read_layer")]
	public static int WzMapReadLayer(
		IntPtr wzPathUtf8, IntPtr mapPathUtf8, int layerIndex,
		byte* outTs, int tsBufferSize, int* outTsMag,
		IntPtr* outTiles, int* outTileCount,
		IntPtr* outObjs, int* outObjCount)
	{
		if (outTs != null)
		{
			WriteFixedUtf8(outTs, tsBufferSize, null);
		}
		*outTsMag = 1;
		*outTiles = IntPtr.Zero;
		*outTileCount = 0;
		*outObjs = IntPtr.Zero;
		*outObjCount = 0;

		try
		{
			Wz_Node? mapNode = FindMapNode(wzPathUtf8, mapPathUtf8);
			Wz_Node? layerNode = mapNode?.Nodes[layerIndex.ToString()];
			if (layerNode == null)
				return 0;

			Wz_Node? infoNode = layerNode.Nodes["info"];
			string? tS = infoNode?.Nodes["tS"].GetValueEx<string>(null);
			if (outTs != null)
			{
				WriteFixedUtf8(outTs, tsBufferSize, tS);
			}
			*outTsMag = infoNode?.Nodes["tSMag"].GetValueEx(1) ?? 1;

			Wz_Node? tileNode = layerNode.Nodes["tile"];
			if (tS != null && tileNode != null)
			{
				var tiles = new List<NativeMapTileItem>();
				foreach (Wz_Node node in tileNode.Nodes)
				{
					NativeMapTileItem item = default;
					WriteFixedUtf8(item.U, 32, node.Nodes["u"].GetValueEx<string>(null));
					item.Index = int.TryParse(node.Text, out int tileIndex) ? tileIndex : 0;
					item.No = node.Nodes["no"].GetValueEx(0);
					item.X = node.Nodes["x"].GetValueEx(0);
					item.Y = node.Nodes["y"].GetValueEx(0);
					item.Zm = node.Nodes["zM"].GetValueEx(0);
					tiles.Add(item);
				}
				*outTiles = AllocArray(tiles, outTileCount);
			}

			Wz_Node? objNode = layerNode.Nodes["obj"];
			if (objNode != null)
			{
				var objs = new List<NativeMapObjItem>();
				foreach (Wz_Node node in objNode.Nodes)
				{
					NativeMapObjItem item = default;
					WriteFixedUtf8(item.Os, 32, node.Nodes["oS"].GetValueEx<string>(null));
					WriteFixedUtf8(item.L0, 32, node.Nodes["l0"].GetValueEx<string>(null));
					WriteFixedUtf8(item.L1, 32, node.Nodes["l1"].GetValueEx<string>(null));
					WriteFixedUtf8(item.L2, 32, node.Nodes["l2"].GetValueEx<string>(null));
					item.Index = int.TryParse(node.Text, out int objIndex) ? objIndex : 0;
					item.X = node.Nodes["x"].GetValueEx(0);
					item.Y = node.Nodes["y"].GetValueEx(0);
					item.Z = node.Nodes["z"].GetValueEx(0);
					item.Zm = node.Nodes["zM"].GetValueEx(0);
					item.F = node.Nodes["f"].GetValueEx(0);
					objs.Add(item);
				}
				*outObjs = AllocArray(objs, outObjCount);
			}

			return 1;
		}
		catch
		{
			*outTiles = IntPtr.Zero;
			*outTileCount = 0;
			*outObjs = IntPtr.Zero;
			*outObjCount = 0;
			return 0;
		}
	}

	// ── wz_map_read_footholds ───────────────────────────────────────────────
	// 맵 .img\foothold\{layer 0~7}\{group}\{id} 전체를 평탄화한 배열로
	// 돌려준다. 수직 선분도 포함(파싱 단계에서는 안 거름).
	// outCount: 아이템 개수(실패/없음 시 0)
	// 반환: NativeFootholdItem 배열(wz_free로 해제), 실패 시 IntPtr.Zero
	[UnmanagedCallersOnly(EntryPoint = "wz_map_read_footholds")]
	public static IntPtr WzMapReadFootholds(IntPtr wzPathUtf8, IntPtr mapPathUtf8, int* outCount)
	{
		*outCount = 0;
		try
		{
			Wz_Node? fhRoot = FindMapNode(wzPathUtf8, mapPathUtf8)?.Nodes["foothold"];
			if (fhRoot == null)
				return IntPtr.Zero;

			var items = new List<NativeFootholdItem>();
			for (int layer = 0; layer <= 7; layer++)
			{
				Wz_Node? layerNode = fhRoot.Nodes[layer.ToString()];
				if (layerNode == null)
					continue;

				foreach (Wz_Node groupNode in layerNode.Nodes)
				{
					if (!int.TryParse(groupNode.Text, out int groupId))
						continue;

					foreach (Wz_Node idNode in groupNode.Nodes)
					{
						if (!int.TryParse(idNode.Text, out int fhId))
							continue;

						NativeFootholdItem item = default;
						item.Id = fhId;
						item.Layer = layer;
						item.Group = groupId;
						item.X1 = idNode.Nodes["x1"].GetValueEx(0);
						item.Y1 = idNode.Nodes["y1"].GetValueEx(0);
						item.X2 = idNode.Nodes["x2"].GetValueEx(0);
						item.Y2 = idNode.Nodes["y2"].GetValueEx(0);
						item.Prev = idNode.Nodes["prev"].GetValueEx(0);
						item.Next = idNode.Nodes["next"].GetValueEx(0);
						item.Piece = idNode.Nodes["piece"].GetValueEx(0);
						items.Add(item);
					}
				}
			}

			return AllocArray(items, outCount);
		}
		catch
		{
			*outCount = 0;
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

	// 마지막으로 연 WZ 루트를 경로 기준으로 캐시한다.
	//
	// 캐시가 없으면 export를 부를 때마다 아카이브를 통째로 다시 연다 — 맵 하나에
	// 타일·오브젝트가 수백 개이고 각각이 캔버스 로딩을 유발하므로, 캐시 없이는
	// 같은 WZ를 수백 번 재오픈하게 된다.
	//
	// 또한 여기서 항상 PluginManager.CurrentRoot를 세팅한다 — AvatarCommon과
	// Wz_NodeExtension2.GetLinkedSourceNode(_outlink/source 해석)가 전부
	// PluginManager.FindWz를 거치고, 그 구현(PluginManagerShim.cs)이 이 값을
	// 기준으로 동작하기 때문이다. 예전엔 wz_read_avatar만 세팅해서, 맵 로딩
	// 경로에서는 링크 해석이 항상 null로 떨어졌다.
	private static string? s_CachedWzPath;
	private static Wz_Node? s_CachedRoot;

	private static Wz_Node? GetRoot(string wzPath)
	{
		if (s_CachedRoot != null && string.Equals(s_CachedWzPath, wzPath, StringComparison.OrdinalIgnoreCase))
		{
			PluginManager.CurrentRoot = s_CachedRoot;
			return s_CachedRoot;
		}

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

		s_CachedWzPath = wzPath;
		s_CachedRoot = root;
		PluginManager.CurrentRoot = root;
		return root;
	}

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