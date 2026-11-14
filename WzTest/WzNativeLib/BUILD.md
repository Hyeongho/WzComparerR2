# WzNativeLib — Native AOT DLL 빌드 방법

## 요구사항
- .NET 8 SDK (Windows x64)
- WzComparerR2 솔루션 루트에서 실행

## 빌드

```powershell
# WzTest/WzNativeLib 폴더에서 실행
cd WzTest\WzNativeLib
dotnet publish -r win-x64 -p:NativeLib=Shared -c Release
```

출력물: `bin\Release\net8.0\win-x64\publish\WzNativeLib.dll`

## 배치

`WzNativeLib.dll` 을 `wz_test.exe` 와 같은 폴더에 복사하면 자동으로 사용됩니다.
DLL 이 없으면 C++ 내장 파서로 폴백합니다.

## 익스포트 함수

| 함수 | 설명 |
|---|---|
| `wz_load_folder(path)` | WZ 폴더 로드 (Skill, Item, Mob 등) |
| `wz_load_file(path)` | 단일 WZ 파일 로드 |
| `wz_free(ptr)` | 반환된 문자열·배열·픽셀 버퍼 해제 |
| `wz_map_read_ladder_ropes(wzPath, mapPath, outCount)` | 맵의 로프·사다리 배치 배열 추출 |

### 로프·사다리 배치 API

`wz_map_read_ladder_ropes`는 `NativeLadderRopeItem` 배열을 반환한다.
각 항목은 `Index, X, Y1, Y2, L, Uf, Page, Piece` 순서의 32비트 정수 8개이며,
`Pack=4`, 항목 크기는 32바이트다. `L=0`은 로프, `L=1`은 사다리다.
노드가 없거나 읽기 실패 시 포인터는 0, 개수는 0이다. 성공 시 C++ 컨테이너에
복사한 뒤 반환 포인터를 `wz_free`로 한 번 해제한다. `info/link`가 있는 맵은
다른 배치 API와 동일하게 연결 대상의 `ladderRope`를 읽는다.

이 API를 사용하려면 위 명령으로 DLL을 다시 publish하고 MapleStory C++ 실행 파일이
사용하는 `WzNativeLib.dll`을 교체해야 한다. 소스 변경만으로 기존 DLL이 갱신되지는 않는다.

## 반환 형식

```
OK
0\tI\tSkillName.img
0\tD\tDragon
1\tI\tDragon.img
```
- 첫 줄: `OK` 또는 `ERR\t<오류메시지>`
- 이후 각 줄: `depth\ttype\tname`
  - depth: 0부터 시작 (0 = 루트 직속 자식)
  - type: `I` (img) 또는 `D` (directory)

## Spine CPU 메시 API (v1)

기존 `wz_map_read_*`와 `wz_anim_load_*`의 구조체/시그니처는 유지한다.
Spine은 `NativeSpineScene.cs`가 기존 spine-monogame 런타임의 2/4 데이터를 해석하고,
화면 출력은 C++ 엔진이 담당한다. 배치의 spineAni와 timeScale을 보존하기 위해
리소스 경로만 받지 않고 원본 맵의 배치 번호로 조회한다.

| export | 역할 |
|---|---|
| wz_spine_api_version() | ABI 버전 1 반환 |
| wz_spine_load_placement(wzPath,mapPath,layer,slot,outMeta) | UInt64 인스턴스 ID 반환, 실패는 0. layer=-1은 back, 0~7은 obj |
| wz_spine_read_page(id,page,outWidth,outHeight,outByteCount) | BGRA atlas 페이지 복사본 반환 |
| wz_spine_update(id,deltaSeconds,outVertices,outCount) | 본/슬롯/메시 갱신. 성공 1, 실패 0. 빈 메시도 성공 |
| wz_spine_last_error() | 마지막 실패 설명의 UTF-8 복사본 반환 |
| wz_spine_destroy(id) | 네이티브 인스턴스와 관리 atlas 데이터 해제 |

페이지, 정점, 오류 문자열 복사본은 각각 wz_free로 해제한다.
NativeSpineMeta는 Pack=4, 40바이트(PageCount,Version,BoundsX/Y/W/H,HasFlowX,FlowX,HasFlowY,FlowY).
NativeSpineVertex는 Pack=4, 40바이트(X,Y,U,V,R,G,B,A float 8개와 Page,Blend int 2개).
정점은 삼각형마다 3개씩 슬롯 순서로 펼친다. Blend는 0=Normal,1=Additive,2=Multiply,3=Screen이다.
메시 좌표는 WZ 원점 기준이며 Y가 아래로 증가한다. 배치 좌표/반전/카메라는 C++이 적용한다.
원본 Spine 리소스의 repeat가 없으면 반복하고 명시적 0이면 마지막 포즈에서 정지한다.
반복은 기존 트랙을 유지한 채 재생 위치를 반복하며, 루프마다 SetAnimation을 다시 호출하지 않는다.
Spine 4는 클리핑을 계산한 메시를 반환한다. 현재 API는 메인 스레드에서 순차 호출한다.

spine-monogame 어셈블리의 메타데이터 참조를 위해 기존 net8 Common.props와 같은
MonoGame.Framework.WindowsDX 3.8.2.1105를 참조한다. GUI 프로젝트 전체는 참조하지 않으며
GraphicsDevice나 MonoGame 렌더러도 사용하지 않는다. Native AOT 출력물을 C++ 실행 폴더에 교체한다.
이번 변경에서는 publish/빌드/실행을 수행하지 않았다.
