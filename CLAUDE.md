# CLAUDE.md - WzComparerR2 C++ WZ 파서 프로젝트

## 프로젝트 개요

**WzComparerR2**는 MapleStory WZ 파일 포맷을 분석하는 C# 도구입니다.
이 세션에서는 WZ 파서를 **C++로 이식**하는 작업을 진행 중입니다.

- **기준 브랜치**: `claude/convert-wz-parser-cpp-cdJ2V`
- **작업 디렉토리**: `/home/user/WzComparerR2`
- **C++ 구현 위치**: `WzTest/wz_test.cpp`

---

## 완료된 작업

### 1단계: WZ 헤더 + 디렉토리 트리 파싱 (완료)
- PKG1 / PKG2 시그니처 파싱
- encverMissing 감지 (KMST1132+ 포맷)
- AES-256-ECB 키스트림 생성 (OpenSSL 의존)
- 암호화 타입 자동 감지 (BMS / KMS / GMS)
- 디렉토리 노드 트리 재귀 파싱

### 2단계: IMG 노드 내부 파싱 (완료)
- IMG 파일 내부의 프로퍼티 트리 파싱 (재귀)
- 지원 노드 타입:
  - `0x00` Null
  - `0x02` UInt16
  - `0x03` CompressedInt32
  - `0x04` CompressedFloat
  - `0x05` Double
  - `0x08` String
  - `0x09` Extended (SubProp, Canvas, Vector, Convex, Sound, UOL)
- WZ 오프셋 계산 및 문자열 참조 (`0x1B` / `0x73`) 처리
- KMST1125 서브 WZ 폴더 로드 및 분할 파일 병합

### 주요 버그 수정 이력
| 커밋 | 내용 |
|---|---|
| `f674a2c` | IMG 노드 미출력 원인 버그 2개 수정 |
| `f9c899c` | `0x02` 노드 문자열 오프셋 계산 버그 수정 |
| `e1c64ac` | KMST1125 분할 WZ 파일 로드 및 merge 구현 |
| `e9b9436` | KMST1125 서브 WZ 폴더 로드 로직 재구현 |

---

## C++ 구현 구조 (wz_test.cpp)

```
WZ_AES_KEY, WZ_IV_KMS, WZ_IV_GMS   -- 암호화 상수
generateKeystream()                 -- AES-256-ECB 키스트림 생성
WzHeader                            -- 헤더 구조체
WzNode                              -- 노드 구조체 (Directory / Image)
WzReader                            -- 파일 읽기 헬퍼 클래스
isLegalNodeName()                   -- 노드 이름 유효성 검사
detectEncryption()                  -- 암호화 타입 자동 감지
readHeader()                        -- WZ 헤더 파싱
readDirTree()                       -- 디렉토리 트리 재귀 파싱
loadSingleWzTree()                  -- 단일 WZ 파일 로드
loadWzFolder()                      -- WZ 폴더 로드 (분할 파일 merge 포함)
printTree()                         -- 노드 트리 출력
main()                              -- 진입점, KMST1125 처리 포함
```

---

## 빌드 방법

```bash
# Linux
g++ -std=c++17 WzTest/wz_test.cpp -lssl -lcrypto -o wz_test

# CMake
mkdir -p WzTest/build && cd WzTest/build
cmake .. && make
```

의존성: `libssl-dev` (OpenSSL)

---

## WZ 파일 포맷 핵심 정보

### 암호화
| 타입 | IV | 설명 |
|---|---|---|
| BMS | 없음 | 암호화 없음 (rolling mask만) |
| KMS | `B9 7D 63 E9` | 한국 메이플 |
| GMS | `4D 23 C7 2B` | 글로벌 메이플 |

### 문자열 인코딩
- `sizeByte < 0`: ASCII (CP1252), rolling mask `0xAA`
- `sizeByte > 0`: UTF-16LE, rolling mask `0xAAAA`

### KMST1125 폴더 구조
```
Data/
  Base/Base.wz         → DIR 목록만 (Character, Effect, Map ...)
  Character/
    Character.wz       → 엔트리
    Character_000.wz   → 분할 파일 (merge 대상)
  Map/
    Map.wz
    Map_000.wz
    ...
```

---

## 다음 작업 제안 (우선순위 순)

### 1. MapData 파싱 (최우선)
WZ img 데이터를 맵 구조체로 변환

```cpp
struct MapData {
    Rect viewport;
    std::vector<BackItem>  backgrounds;
    std::vector<LayerData> layers;   // [0~7] Obj + Tile + Foothold
    std::vector<Portal>    portals;
    std::vector<Life>      life;     // NPC / 몬스터
};
```

참고: C# `WzComparerR2.MapRender/MapData.cs`

### 2. 텍스처 로드 (Canvas → 픽셀)
WZ img Canvas 노드를 실제 픽셀 데이터로 변환

| 포맷 번호 | 설명 |
|---|---|
| 1 | BGRA4444 |
| 2 | BGRA8888 |
| 517 | 압축 (DXT 계열) |
| 1026, 2050 | BC3 / BC7 압축 |

참고: C# `WzComparerR2.WzLib/Wz_Png.cs`

### 3. 렌더링 라이브러리 선택 + 카메라
- 후보: SDL2+OpenGL, SFML, raylib, bgfx
- 카메라: 월드 ↔ 스크린 좌표 변환

### 4. 기본 렌더링 루프
타일, 배경 오브젝트 렌더링

### 5. 애니메이션 시스템
FrameAnimator (딜레이 기반 프레임 전환)

### 6. Foothold 충돌

### 7. 캐릭터 / 포탈 / Life

---

## 참고 C# 소스 위치

| 기능 | C# 파일 |
|---|---|
| WZ 파일 읽기 | `WzComparerR2.WzLib/Wz_File.cs` |
| 암호화 | `WzComparerR2.WzLib/Wz_Crypto.cs` |
| IMG 파싱 | `WzComparerR2.WzLib/Wz_Image.cs` |
| 맵 렌더링 | `WzComparerR2.MapRender/` |
| 카메라 | `WzComparerR2.MapRender/Camera.cs` |
