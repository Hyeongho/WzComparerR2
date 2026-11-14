# 사운드 추출 API

`SoundExports.cs`는 WZ 사운드 바이트와 맵 BGM 경로만 제공한다. 재생 장치를 열거나 PCM 디코딩을 수행하지 않는다. C++ 엔진에서 Media Foundation과 XAudio2를 사용한다.

## ABI

```cpp
// UTF-8 경로, win-x64. 반환값은 각각 wz_free로 해제한다.
const unsigned char* wz_read_sound(const char* wzPath, const char* nodePath, int* outLen);
const char* wz_map_read_bgm(const char* wzPath, const char* mapPath);
void wz_free(void* buffer);
```

- `wz_read_sound`: MP3 원본 또는 RIFF/WAVE 파일 바이트를 반환한다. PCM에는 Wz_Sound.ExtractSound()가 WAV 헤더를 붙인다. 실패 시 null과 길이 0이다. Binary/Unknown 사운드는 반환하지 않는다.
- `wz_map_read_bgm`: 요청한 맵 info/bgm을 `Sound\Bgm00.img\곡이름` 형태로 정규화한 null 종료 UTF-8 문자열을 반환한다. 정보가 없거나 실패하면 null이다.
- `Sound\Bgm00.img\곡이름`, `Bgm00/곡이름` 형식을 지원한다. Base.wz/데이터 폴더를 통한 통합 루트 또는 Sound.wz 자체에서 사운드를 찾는다. 맵 BGM 조회에는 Map과 Sound가 함께 보이는 루트를 사용한다.
- UOL은 최대 32단계까지 해석한다. multi BGM 컨테이너는 MapRender2의 기본 선택처럼 첫 Sound/UOL을 선택한다. 상황에 따른 후보 전환은 내보내지 않는다.
- 기존 NativeMapInfo나 다른 맵 구조체는 확장하지 않는다. 사운드 API가 없는 구 DLL에서는 C++ 오디오 로더만 실패하도록 별도 export로 구성했다.
- 기존 GetRoot/PluginManager.CurrentRoot 캐시를 사용하므로 다른 WZ API와 함께 호출 스레드를 직렬화한다. 현재 Game은 게임 스레드에서만 로딩한다.

## 게시·배포

빌드는 사용자가 수행한다. WzNativeLib 디렉터리에서 기존과 같은 방식으로 게시한다.

```powershell
dotnet publish WzNativeLib.csproj -r win-x64 -c Release -p:NativeLib=Shared
```

`bin/Release/net8.0/win-x64/publish/WzNativeLib.dll`을 실제 Game 실행 환경의 DLL로 교체한다. 엔진 소스 변경만으로 기존 DLL에 새 export가 생기지는 않는다. 이번 작업에서 DLL 게시·실행은 수행하지 않았다.
