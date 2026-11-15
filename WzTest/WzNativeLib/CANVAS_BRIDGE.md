# 링크된 UI Canvas 추출

`wz_read_canvas`의 기존 8개 인자와 BGRA8888 반환 규약은 유지한다. `CanvasResources.cs`에 경로·UOL·이미지 링크 해석을 추가하고 `wz_canvas_api_version` export가 2를 반환하도록 했다. C++ UI 로더는 구 DLL을 감지해 잘못된 1x1 UI 표시 대신 단색 HUD를 유지한다.

## 해석 규칙

- 경로의 `/`를 `\`로 정규화하고 IMG를 추출한다. `UI\...` 경로는 Base.wz 통합 루트와 UI.wz 자체의 루트를 지원한다.
- UOL은 최대 32단계까지 해석한다. 상대 링크는 기존 Wz_Uol.HandleUol, 절대 링크는 WZ 루트에서 찾는다.
- UOL 해석 후의 원본 Canvas에서 origin과 delay를 읽는다.
- 픽셀은 source → _inlink → _outlink 우선순위로 찾는다. 링크 대상의 UOL·추가 이미지 링크도 해석하며 링크 체인은 최대 32단계다.
- _inlink는 현재 IMG 안에서, source/_outlink는 WZ 루트에서 찾는다. 링크를 찾지 못하거나 순환하면 실패를 반환한다. 참조용 1x1 PNG를 대신 반환하지 않는다.
- 성공 시 실제 이미지의 width/height와 원본 origin/delay를 반환한다. 네이티브 버퍼는 wz_free로 해제한다. 실패 시 출력 크기·길이는 0으로 초기화하며 이미 할당한 버퍼도 해제한다.

사용자가 제공한 UI.StatusBar3.img.xml의 주요 PNG는 1x1이고 `_outlink`로 UI/_Canvas/StatusBar3.img의 실제 이미지를 참조한다. XML만으로 실제 그림을 복원하지 않으며, 실행 시 WZ에 대상 리소스가 있어야 한다. StatusBar3 전체 화면이나 버튼 동작을 DLL에서 만들지는 않는다.

## 게시

WzExports.cs와 CanvasResources.cs를 모두 반영하고 기존 WzNativeLib 프로젝트를 게시한다. SDK 프로젝트의 기본 소스 포함 규칙을 사용하므로 새 cs 파일을 별도 Compile 항목으로 등록할 필요는 없다.

```powershell
dotnet publish WzNativeLib.csproj -r win-x64 -c Release -p:NativeLib=Shared
```

publish/WzNativeLib.dll을 실제 Game 실행 폴더에 교체하고 Game을 재실행한다. 이번 작업에서 게시·빌드·실행은 수행하지 않았다. 기존 오디오·맵·아바타 export의 ABI는 변경하지 않았다.
