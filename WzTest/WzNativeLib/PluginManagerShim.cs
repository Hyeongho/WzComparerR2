// PluginManagerShim.cs — 실제 WzComparerR2.PluginBase 프로젝트(WinForms GUI 앱
// 전역 이벤트 기반 레지스트리)를 링크하지 않고, 그 대신 우리가 직접 구현한
// 대체 PluginManager.
//
// AvatarCanvas/AvatarPart 등 AvatarCommon 소스는 내부적으로
// `PluginBase.PluginManager.FindWz(...)`로 WZ 노드를 찾는데, 실제
// PluginManager는 GUI 앱이 켜지면서 로드된 WZ 파일들을 검색하는 핸들러를
// `internal static event WzFileFinding`에 등록해두는 구조라 헤드리스 DLL에는
// 안 맞는다(그 이벤트 자체가 internal이라 다른 어셈블리에서 구독도 불가능).
//
// 대신 FindWz(string)은 결국 "경로 문자열 → Wz_Node" 변환일 뿐이므로,
// wz_read_avatar가 미리 CurrentRoot에 세팅해둔 루트 Wz_Node에서
// FindNodeByPath로 직접 찾아주는 걸로 완전히 대체 가능하다.
using WzComparerR2.WzLib;

namespace WzComparerR2.PluginBase
{
	public static class PluginManager
	{
		// wz_read_avatar 진입 시 OpenWzPathAndGetRoot 결과로 세팅한다.
		public static Wz_Node? CurrentRoot { get; set; }

		public static Wz_Node? FindWz(string fullPath)
		{
			return CurrentRoot?.FindNodeByPath(fullPath);
		}

		public static Wz_Node? FindWz(string fullPath, Wz_File? sourceWzFile)
		{
			// sourceWzFile 무시 — 우리는 KMST 병합 루트 하나만 쓴다.
			return FindWz(fullPath);
		}

		public static Wz_Node? FindWz(Wz_Type type, Wz_File? sourceWzFile = null, bool hasChildNodes = false)
		{
			// AvatarCanvasManager 대체 코드(FindNodeByGearID 등)에서 쓰는
			// Wz_Type.Character 조회만 지원 — 지금 필요한 게 이것뿐이라 그 외는
			// null 반환(실제 PluginManager도 핸들러 미등록 시 null 반환하므로
			// 동일한 "안전하게 실패" 동작).
			return type == Wz_Type.Character ? CurrentRoot?.FindNodeByPath("Character") : null;
		}
	}
}
