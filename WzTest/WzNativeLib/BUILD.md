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
| `wz_free(ptr)` | 반환된 문자열 해제 |

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
