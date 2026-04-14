#pragma execution_character_set("utf-8")
// wz_test.cpp — WzNativeLib.dll 로더 + 트리 덤프 + IMG 추출 테스트
//
// 빌드 (MSVC):
//   cl /EHsc /std:c++17 /utf-8 wz_test.cpp
//
// 실행:
//   wz_test.exe  <WzNativeLib.dll 경로>  <WZ 파일/폴더 경로>  [IMG 이름]  [저장 경로]
//
// 예시:
//   wz_test.exe WzNativeLib.dll "C:\Maple\Data\Skill.wz"
//   wz_test.exe WzNativeLib.dll "C:\Maple\Data\Skill.wz" "DragonSkill.img" out.img

#include <windows.h>
#include <iostream>
#include <fstream>
#include <string>
#include <cstring>
#include <filesystem>

namespace fs2 = std::filesystem;

// ── DLL 함수 포인터 ─────────────────────────────────────────────────────────
namespace WzDll
{
    // wz_open: 포맷 자동 감지 (.ms/.mn/KMST1125/일반 WZ)
    using FnOpen    = const char*(*)(const char* pathUtf8);
    // wz_load_folder: WZ 폴더 로드
    using FnFolder  = const char*(*)(const char* folderPathUtf8);
    // wz_load_file: 단일 WZ 파일 로드
    using FnFile    = const char*(*)(const char* filePathUtf8);
    // wz_read_img: IMG raw 바이트 반환 (실패 시 nullptr, outLen=0)
    using FnReadImg = const uint8_t*(*)(const char* wzPath,
                                        const char* imgPath,
                                        int*        outLen);
    // wz_free: 위 함수들이 반환한 포인터 해제
    using FnFree    = void(*)(const char* ptr);

    static HMODULE    hDll      = nullptr;
    static FnOpen     openWz    = nullptr;
    static FnFolder   loadFolder = nullptr;
    static FnFile     loadFile  = nullptr;
    static FnReadImg  readImg   = nullptr;
    static FnFree     freeResult = nullptr;

    static bool tryLoad(const std::string& dllPath)
    {
        hDll = LoadLibraryA(dllPath.c_str());
        if (!hDll) {
            std::cerr << "[WzDll] LoadLibrary 실패: " << dllPath
                      << "  (오류=" << GetLastError() << ")\n";
            return false;
        }

        openWz     = (FnOpen)    GetProcAddress(hDll, "wz_open");
        loadFolder = (FnFolder)  GetProcAddress(hDll, "wz_load_folder");
        loadFile   = (FnFile)    GetProcAddress(hDll, "wz_load_file");
        readImg    = (FnReadImg) GetProcAddress(hDll, "wz_read_img");
        freeResult = (FnFree)    GetProcAddress(hDll, "wz_free");

        // 필수 함수 확인
        const char* missing = nullptr;
        if (!openWz)     missing = "wz_open";
        else if (!loadFolder) missing = "wz_load_folder";
        else if (!loadFile)   missing = "wz_load_file";
        else if (!freeResult) missing = "wz_free";

        if (missing) {
            std::cerr << "[WzDll] 함수 없음: " << missing
                      << " — Native AOT(Publish)로 빌드된 DLL인지 확인하세요.\n";
            FreeLibrary(hDll);
            hDll = nullptr;
            return false;
        }

        // wz_read_img는 선택적 (구 DLL 호환)
        if (!readImg)
            std::cerr << "[WzDll] wz_read_img 없음 — IMG 추출 불가 (DLL 재빌드 필요)\n";

        return true;
    }
} // namespace WzDll

// ── 트리 출력 (콘솔 + 파일 동시 출력) ─────────────────────────────────────
// log: 파일 스트림 (nullptr이면 파일 출력 생략)
static void printTree(const char* result, std::ofstream* log = nullptr)
{
    // 콘솔과 파일에 동시에 한 줄 출력하는 람다
    auto writeLine = [&](const std::string& line) {
        std::cout << line << "\n";
        if (log && log->is_open()) *log << line << "\n";
    };

    if (!result) { std::cerr << "[오류] null 반환\n"; return; }

    std::string s(result);
    // 첫 줄이 ERR 이면 오류 출력
    if (s.rfind("ERR\t", 0) == 0) {
        std::cerr << "[WZ 오류] " << s.substr(4) << "\n";
        return;
    }

    // OK 이후 각 줄: "depth\ttype\tname"
    size_t pos = s.find('\n');
    if (pos == std::string::npos) return;
    pos++;  // OK 줄 건너뜀

    int imgCount = 0, dirCount = 0;
    while (pos < s.size()) {
        size_t nl = s.find('\n', pos);
        if (nl == std::string::npos) nl = s.size();
        std::string line = s.substr(pos, nl - pos);
        if (!line.empty() && line.back() == '\r') line.pop_back();

        if (!line.empty()) {
            // depth\ttype\tname
            size_t t1 = line.find('\t');
            size_t t2 = (t1 != std::string::npos) ? line.find('\t', t1 + 1) : std::string::npos;
            if (t1 != std::string::npos && t2 != std::string::npos) {
                int depth = std::stoi(line.substr(0, t1));
                char type = line[t1 + 1];
                std::string name = line.substr(t2 + 1);
                std::string indent(depth * 2, ' ');
                writeLine(indent + (type == 'I' ? "[IMG] " : "[DIR] ") + name);
                if (type == 'I') imgCount++; else dirCount++;
            }
        }
        pos = nl + 1;
    }

    std::string summary = "총 " + std::to_string(imgCount + dirCount)
                        + " 개 항목  (IMG: " + std::to_string(imgCount)
                        + ", DIR: " + std::to_string(dirCount) + ")";
    writeLine(summary);
}

// ── IMG 추출 ────────────────────────────────────────────────────────────────
static bool extractImgToFile(const std::string& wzPath,
                              const std::string& imgPath,
                              const std::string& outPath)
{
    if (!WzDll::readImg) {
        std::cerr << "[추출] wz_read_img 없음\n";
        return false;
    }

    int len = 0;
    const uint8_t* data = WzDll::readImg(wzPath.c_str(), imgPath.c_str(), &len);
    if (!data || len == 0) {
        std::cerr << "[추출] 실패 — IMG 노드를 찾을 수 없거나 읽기 오류\n";
        return false;
    }

    std::ofstream ofs(outPath, std::ios::binary);
    if (!ofs) {
        std::cerr << "[추출] 파일 쓰기 실패: " << outPath << "\n";
        WzDll::freeResult((const char*)data);
        return false;
    }
    ofs.write(reinterpret_cast<const char*>(data), len);
    WzDll::freeResult((const char*)data);

    std::cout << "[추출] 완료: " << outPath << "  (" << len << " bytes)\n";
    return true;
}

// ── main ────────────────────────────────────────────────────────────────────
int main(int argc, char* argv[])
{
    SetConsoleOutputCP(CP_UTF8);

    // ── 기본값 (인자 없을 때 사용) ─────────────────────────────────────────
    std::string dllPath = "WzNativeLib.dll";
    std::string wzPath  = R"(C:\Nexon\MapleStory\Data\Skill.wz)";
    std::string imgPath = "";   // IMG 추출할 경우 파일명 (예: "000.img")
    std::string outPath = "";   // 저장 경로 (비어 있으면 imgPath 이름으로 저장)

    // 명령줄 인자가 있으면 덮어씀
    if (argc >= 2) dllPath = argv[1];
    if (argc >= 3) wzPath  = argv[2];
    if (argc >= 4) imgPath = argv[3];
    if (argc >= 5) outPath = argv[4];

    if (!WzDll::tryLoad(dllPath)) return 1;

    // ── 로그 파일 열기 (WZ 파일명_tree.txt, 실행 폴더에 저장)
    std::string logName = fs2::path(wzPath).stem().string() + "_tree.txt";
    std::ofstream logFile(logName, std::ios::out);
    if (!logFile.is_open())
        std::cerr << "[경고] 로그 파일 열기 실패: " << logName << " (콘솔만 출력)\n";
    else
        std::cout << "[로그] " << logName << " 에 저장됩니다.\n";

    // ── 트리 덤프 (콘솔 + 파일 동시)
    std::string header = "=== WZ 트리: " + wzPath + " ===";
    std::cout << header << "\n";
    if (logFile.is_open()) logFile << header << "\n";

    const char* result = WzDll::openWz(wzPath.c_str());
    printTree(result, logFile.is_open() ? &logFile : nullptr);
    WzDll::freeResult(result);

    // ── IMG 추출 (인자가 있을 때)
    if (!imgPath.empty()) {
        std::string savePath = outPath.empty() ? imgPath : outPath;
        // imgPath에 경로 구분자 포함 가능 (예: "Skill\Dragon.img")
        extractImgToFile(wzPath, imgPath, savePath);
    }

    FreeLibrary(WzDll::hDll);
    return 0;
}
