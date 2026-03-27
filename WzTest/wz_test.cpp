/**
 * wz_test.cpp - WZ 파일 파서 콘솔 테스트
 *
 * WZ 파일을 열어 헤더 정보와 노드 트리를 출력합니다.
 * WzComparerR2.WzLib의 C# 코드를 기반으로 구현한 독립 C++ 프로그램입니다.
 *
 * 빌드:
 *   Linux:   g++ -std=c++17 wz_test.cpp -lssl -lcrypto -o wz_test
 *   Windows: cl /std:c++17 wz_test.cpp /link libssl.lib libcrypto.lib
 *
 * 테스트 경로 (고정):
 *   C:\Nexon\Maple\Data\Base\Base.wz
 */

#include <iostream>
#include <fstream>
#include <sstream>
#include <iomanip>
#include <string>
#include <vector>
#include <cstdint>
#include <cstring>
#include <stdexcept>
#include <filesystem>
#include <regex>

// AES 의존성 (libssl-dev / openssl)
#include <openssl/evp.h>

// ================================================================
// 상수 및 암호화 키
// ================================================================

// AES-256 키 (WZ 포맷 고정값)
static const uint8_t WZ_AES_KEY[32] = {
    0x13, 0x00, 0x00, 0x00,
    0x08, 0x00, 0x00, 0x00,
    0x06, 0x00, 0x00, 0x00,
    0xB4, 0x00, 0x00, 0x00,
    0x1B, 0x00, 0x00, 0x00,
    0x0F, 0x00, 0x00, 0x00,
    0x33, 0x00, 0x00, 0x00,
    0x52, 0x00, 0x00, 0x00
};

// KMS IV, GMS IV
static const uint8_t WZ_IV_KMS[4] = { 0xB9, 0x7D, 0x63, 0xE9 };
static const uint8_t WZ_IV_GMS[4] = { 0x4D, 0x23, 0xC7, 0x2B };

static const char* WZ_SIG_PKG1 = "PKG1";
static const char* WZ_SIG_PKG2 = "PKG2";

enum class CryptoType { BMS, KMS, GMS, UNKNOWN };

// ================================================================
// AES 키스트림 생성
//
// C# Wz_CryptoKey.EnsureKeySize() 동일 구현:
//   block[0] = AES_ECB_encrypt(IV repeated 4x)
//   block[n] = AES_ECB_encrypt(block[n-1])
// ================================================================
std::vector<uint8_t> generateKeystream(const uint8_t* iv4, size_t size) {
    size_t bufSize = (size + 15) & ~(size_t)15;
    std::vector<uint8_t> ks(bufSize, 0);

    uint8_t block[16];
    for (int i = 0; i < 16; i++) block[i] = iv4[i % 4];

    EVP_CIPHER_CTX* ctx = EVP_CIPHER_CTX_new();
    EVP_EncryptInit_ex(ctx, EVP_aes_256_ecb(), nullptr, WZ_AES_KEY, nullptr);
    EVP_CIPHER_CTX_set_padding(ctx, 0);

    int outLen = 0;
    for (size_t offset = 0; offset < bufSize; offset += 16) {
        EVP_EncryptUpdate(ctx, &ks[offset], &outLen, block, 16);
        // 다음 블록 입력 = 현재 출력 (체이닝)
        memcpy(block, &ks[offset], 16);
    }
    EVP_CIPHER_CTX_free(ctx);

    ks.resize(size);
    return ks;
}

// ================================================================
// WZ 헤더
// ================================================================
struct WzHeader {
    std::string signature;    // "PKG1" or "PKG2"
    int64_t     dataSize;
    int32_t     headerSize;
    std::string copyright;
    int32_t     dataStartPosition;
    bool        encverMissing; // KMST1132+ (PKG1만 해당)
    int32_t     wzVersion;     // 검출된 버전 (0=미검출)
};

// ================================================================
// 노드 구조체
// ================================================================
enum class WzNodeType { Directory, Image };

struct WzNode {
    std::string name;
    WzNodeType  type;
    int         childCount = 0;
    std::vector<WzNode> children;
};

// ================================================================
// WzReader - 파일에서 WZ 포맷 데이터 읽기
// ================================================================
class WzReader {
public:
    explicit WzReader(std::ifstream& f) : file(f) {}

    std::streampos tell() { return file.tellg(); }
    void seek(std::streampos pos) { file.seekg(pos); }

    bool good() const { return file.good(); }

    uint8_t  readU8()  { uint8_t  v; file.read((char*)&v, 1); return v; }
    int8_t   readS8()  { int8_t   v; file.read((char*)&v, 1); return v; }
    uint16_t readU16() { uint16_t v; file.read((char*)&v, 2); return v; }
    int32_t  readS32() { int32_t  v; file.read((char*)&v, 4); return v; }
    uint32_t readU32() { uint32_t v; file.read((char*)&v, 4); return v; }
    int64_t  readS64() { int64_t  v; file.read((char*)&v, 8); return v; }

    // WZ Compressed Int32: sbyte; if sbyte == -128, read full int32
    int32_t readCompressedInt32() {
        int8_t s = readS8();
        return (s == -128) ? readS32() : (int32_t)s;
    }

    std::string readRawBytes(int size) {
        std::string buf(size, '\0');
        file.read(&buf[0], size);
        return buf;
    }

    std::string readAsciiChars(int count) {
        std::string buf(count, '\0');
        file.read(&buf[0], count);
        return buf;
    }

    // WZ String 읽기 (WzBinaryReader.ReadString 동일 구현)
    // cryptoKey: 빈 벡터 = BMS(no-op), 채워진 벡터 = KMS/GMS 키스트림
    std::string readString(const std::vector<uint8_t>& cryptoKey) {
        int8_t sizeByte = readS8();
        if (sizeByte == 0) return "";

        if (sizeByte < 0) {
            // ASCII (CP1252) 문자열
            int size = (sizeByte == -128) ? readS32() : (int)-sizeByte;
            std::vector<uint8_t> buf(size);
            file.read((char*)buf.data(), size);

            // 1. Decrypt (crypto key XOR)
            for (int i = 0; i < size && i < (int)cryptoKey.size(); i++)
                buf[i] ^= cryptoKey[i];

            // 2. Rolling mask XOR (0xAA, 0xAB, ...)
            std::string result(size, '\0');
            uint8_t mask = 0xAA;
            for (int i = 0; i < size; i++) {
                result[i] = (char)(buf[i] ^ mask);
                mask++;
            }
            return result;
        } else {
            // UTF-16LE 문자열
            int size = (sizeByte == 127) ? readS32() : (int)sizeByte;
            std::vector<uint16_t> buf(size);
            file.read((char*)buf.data(), size * 2);

            // 1. Decrypt (crypto key XOR, byte-wise)
            uint8_t* rawBuf = (uint8_t*)buf.data();
            for (int i = 0; i < size * 2 && i < (int)cryptoKey.size(); i++)
                rawBuf[i] ^= cryptoKey[i];

            // 2. Rolling mask XOR (0xAAAA, 0xAAAB, ...)
            std::string result;
            uint16_t mask = 0xAAAA;
            for (int i = 0; i < size; i++) {
                uint16_t c = buf[i] ^ mask;
                mask++;
                // UTF-16 → UTF-8 변환 (기본 다국어 평면만)
                if (c < 0x80) {
                    result += (char)c;
                } else if (c < 0x800) {
                    result += (char)(0xC0 | (c >> 6));
                    result += (char)(0x80 | (c & 0x3F));
                } else {
                    result += (char)(0xE0 | (c >> 12));
                    result += (char)(0x80 | ((c >> 6) & 0x3F));
                    result += (char)(0x80 | (c & 0x3F));
                }
            }
            return result;
        }
    }

    // 지정 오프셋에서 문자열 읽기 (현재 위치 복원)
    // offset: DataStartPosition 기준 상대 오프셋
    std::string readStringAt(int64_t absoluteFileOffset,
                             const std::vector<uint8_t>& cryptoKey) {
        auto saved = tell();
        seek(absoluteFileOffset);
        auto s = readString(cryptoKey);
        seek(saved);
        return s;
    }

private:
    std::ifstream& file;
};

// ================================================================
// 노드 이름 유효성 검사 (Wz_Crypto.IsLegalNodeName 동일)
// ================================================================
bool isLegalNodeName(const std::string& name) {
    if (name.size() >= 4) {
        auto ext = name.substr(name.size() - 4);
        if (ext == ".img" || ext == ".lua") return true;
    }
    for (unsigned char c : name) {
        if (c < 0x20 || c > 0x7F) return false;
    }
    return !name.empty();
}

// ================================================================
// 암호화 타입 자동 감지 (Wz_Crypto.DetectEncryption 동일 로직)
// ================================================================
CryptoType detectEncryption(WzReader& reader, const WzHeader& header,
                             const std::vector<uint8_t>& ksKMS,
                             const std::vector<uint8_t>& ksGMS) {
    // 첫 번째 노드의 이름 문자열로 암호화 판단
    reader.seek(header.dataStartPosition);
    int nodeCount = reader.readCompressedInt32();
    if (nodeCount <= 0) return CryptoType::BMS;

    // type byte 건너뜀
    reader.readU8();

    int8_t sizeByte = reader.readS8();
    if (sizeByte >= 0) return CryptoType::BMS; // UTF-16 → 건너뜀

    int size = (sizeByte == -128) ? reader.readS32() : (int)-sizeByte;
    if (size <= 0 || size > 256) return CryptoType::BMS;

    std::vector<uint8_t> rawBytes(size);
    reader.readRawBytes(size); // 실제로 파일에서 읽음

    // 현재 위치에서 size 바이트 다시 읽기 위해 뒤로 감
    auto curPos = (int64_t)reader.tell() - size;
    reader.seek(curPos);
    for (int i = 0; i < size; i++) rawBytes[i] = reader.readU8();

    // rolling mask만 적용 (BMS)
    std::string bmsTry(size, '\0');
    uint8_t mask = 0xAA;
    for (int i = 0; i < size; i++) {
        bmsTry[i] = (char)(rawBytes[i] ^ mask);
        mask++;
    }
    if (isLegalNodeName(bmsTry)) return CryptoType::BMS;

    // KMS 키스트림 추가 적용
    std::string kmsTry(size, '\0');
    mask = 0xAA;
    for (int i = 0; i < size; i++) {
        uint8_t dec = rawBytes[i];
        if (i < (int)ksKMS.size()) dec ^= ksKMS[i];
        kmsTry[i] = (char)(dec ^ mask);
        mask++;
    }
    if (isLegalNodeName(kmsTry)) return CryptoType::KMS;

    // GMS 키스트림 추가 적용
    std::string gmsTry(size, '\0');
    mask = 0xAA;
    for (int i = 0; i < size; i++) {
        uint8_t dec = rawBytes[i];
        if (i < (int)ksGMS.size()) dec ^= ksGMS[i];
        gmsTry[i] = (char)(dec ^ mask);
        mask++;
    }
    if (isLegalNodeName(gmsTry)) return CryptoType::GMS;

    return CryptoType::BMS; // 판단 불가 시 BMS로 폴백
}

// ================================================================
// WZ 헤더 읽기 (Wz_File.GetHeader 동일 로직)
// ================================================================
WzHeader readHeader(WzReader& reader, const std::string& filePath) {
    reader.seek(0);
    WzHeader header{};

    // 파일 시그니처 확인
    std::string sig = reader.readAsciiChars(4);
    if (sig != WZ_SIG_PKG1 && sig != WZ_SIG_PKG2) {
        throw std::runtime_error("올바른 WZ 파일이 아닙니다. (시그니처: '" + sig + "')");
    }
    header.signature = sig;

    header.dataSize   = reader.readS64();
    header.headerSize = reader.readS32();

    // 저작권 문자열
    int copyrightLen = header.headerSize - (int)reader.tell();
    if (copyrightLen > 0)
        header.copyright = reader.readAsciiChars(copyrightLen);

    if (sig == WZ_SIG_PKG1) {
        // encver 존재 여부 판단 (KMST1132+ = encverMissing)
        bool encverMissing = false;

        if (header.dataSize >= 2) {
            reader.seek(header.headerSize);
            uint16_t encver = reader.readU16();
            if (encver > 0xFF) {
                encverMissing = true;
            } else if (encver == 0x80 && header.dataSize >= 5) {
                // 특수 케이스: compressed int 첫 바이트가 0x80인 경우
                reader.seek(header.headerSize);
                int8_t firstByte = reader.readS8();
                if (firstByte == -128) {
                    int32_t propCount = reader.readS32();
                    if (propCount > 0 && (propCount & 0xFF) == 0 && propCount <= 0xFFFF)
                        encverMissing = true;
                }
            }
        } else {
            encverMissing = true;
        }

        header.encverMissing = encverMissing;
        header.dataStartPosition = header.headerSize + (encverMissing ? 0 : 2);
    } else {
        // PKG2
        header.encverMissing = false;
        reader.seek(header.headerSize);
        /* uint32_t hash1 = */ reader.readU32();
        /* uint32_t hash2 = */ reader.readU32();
        header.dataStartPosition = (int32_t)reader.tell();
    }

    return header;
}

// ================================================================
// 디렉토리 트리 읽기 (Wz_File.ReadDirTree PKG1 동일 로직)
// ================================================================
WzNode readDirTree(WzReader& reader,
                   const WzHeader& header,
                   const std::vector<uint8_t>& cryptoKey) {
    WzNode root;
    int count = reader.readCompressedInt32();

    std::vector<std::string> dirNames; // 하위 디렉토리 이름 (순서 유지)

    for (int i = 0; i < count; i++) {
        uint8_t nodeType = reader.readU8();
        std::string name;

        switch (nodeType) {
            case 0x02: {
                // 별도 오프셋에 있는 문자열 참조
                // C# 는 PartialStream(DataStartPosition 기준)을 사용하므로
                // strOff 는 DataStartPosition 기준 상대 오프셋 → 절대 오프셋으로 변환
                int32_t strOff = reader.readS32();
                int32_t adjust = header.encverMissing ? 2 : -1;
                int64_t absOff = (int64_t)header.dataStartPosition + strOff + adjust;
                name = reader.readStringAt(absOff, cryptoKey);
                break;
            }
            case 0x03:
            case 0x04:
                name = reader.readString(cryptoKey);
                break;
            default:
                throw std::runtime_error(
                    "알 수 없는 노드 타입: 0x" +
                    (std::ostringstream() << std::hex << (int)nodeType).str()
                );
        }

        /*int32_t size  =*/ reader.readCompressedInt32(); // img 크기 (건너뜀)
        /*int32_t cs32  =*/ reader.readCompressedInt32(); // 체크섬 (건너뜀)
        /*uint32_t hoff =*/ reader.readU32();              // hash offset (건너뜀)

        WzNode node;
        node.name = name;

        if (nodeType == 0x02 || nodeType == 0x04) {
            node.type = WzNodeType::Image;
        } else { // 0x03
            node.type = WzNodeType::Directory;
            dirNames.push_back(name);
        }
        root.children.push_back(std::move(node));
    }

    // 하위 디렉토리 재귀 읽기 (reader 위치를 순서대로 소비)
    // depth 제한 없이 항상 재귀 — depth 제한을 걸면 소비되지 않은 데이터 블록이
    // 남아 reader 위치가 틀어져 이후 형제 디렉토리 읽기가 깨진다 (C# 도 동일)
    for (auto& dirName : dirNames) {
        WzNode subTree = readDirTree(reader, header, cryptoKey);
        subTree.name = dirName;
        for (auto& child : root.children) {
            if (child.name == dirName && child.type == WzNodeType::Directory) {
                child.children = std::move(subTree.children);
                break;
            }
        }
    }

    root.childCount = (int)root.children.size();
    return root;
}

// ================================================================
// PKG2 디렉토리 트리 읽기 (Wz_File.ReadDirTreePkg2 동일 로직)
//
// PKG1 과의 핵심 차이:
//   PKG1 엔트리: [nodeType][name][size][cs32][hashOffset(4B)]
//   PKG2 엔트리: [nodeType][name][size][cs32]  ← hashOffset 없음!
//               hashOffset 은 엔트리 목록 뒤에 별도 섹션으로 모아서 읽음
//
// 루프 종료 조건 (terminator):
//   nodeType == 0x80
//   OR (encryptedEntryCount 가 [-127,127] 범위이고
//       nodeType == (uint8_t)(int8_t)encryptedEntryCount)
// ================================================================
WzNode readDirTreePkg2(WzReader& reader, const std::vector<uint8_t>& cryptoKey) {
    WzNode root;

    int32_t encryptedEntryCount = reader.readCompressedInt32();

    // 엔트리 임시 저장 (hashOffset 은 뒤에서 읽음)
    struct Pkg2Entry {
        uint8_t     nodeType;
        std::string name;
        int32_t     size;
        int32_t     checksum;
    };
    std::vector<Pkg2Entry> entries;

    // 엔트리 목록 읽기 (terminator 까지)
    while (true) {
        uint8_t nodeType = reader.readU8();

        if (nodeType == 0x03 || nodeType == 0x04) {
            std::string name = reader.readString(cryptoKey);
            int32_t size = reader.readCompressedInt32();
            int32_t cs32 = reader.readCompressedInt32();
            entries.push_back({ nodeType, name, size, cs32 });
        } else if (nodeType == 0x80 ||
                   (encryptedEntryCount >= -127 && encryptedEntryCount <= 127 &&
                    nodeType == static_cast<uint8_t>(static_cast<int8_t>(encryptedEntryCount)))) {
            // terminator: 1바이트 되돌림 (C# 동일)
            reader.seek(reader.tell() - std::streamoff(1));
            break;
        } else {
            throw std::runtime_error(
                "알 수 없는 PKG2 노드 타입: 0x" +
                (std::ostringstream() << std::hex << (int)nodeType).str()
            );
        }
    }

    // encryptedOffsetCount 읽기 — encryptedEntryCount 와 일치해야 hashOffset 섹션 유효
    int32_t encryptedOffsetCount = reader.readCompressedInt32();

    std::vector<std::string> dirNames;

    if (encryptedOffsetCount == encryptedEntryCount && !entries.empty()) {
        for (auto& entry : entries) {
            /*uint32_t hashOffset =*/ reader.readU32(); // hashOffset (건너뜀)
            // pos = hashOffset 읽은 직후 (C# 설계 동일)

            WzNode node;
            node.name = entry.name;
            if (entry.nodeType == 0x04) {
                node.type = WzNodeType::Image;
            } else { // 0x03
                node.type = WzNodeType::Directory;
                dirNames.push_back(entry.name);
            }
            root.children.push_back(std::move(node));
        }
    }

    // 하위 디렉토리 재귀 읽기
    for (auto& dirName : dirNames) {
        WzNode subTree = readDirTreePkg2(reader, cryptoKey);
        subTree.name = dirName;
        for (auto& child : root.children) {
            if (child.name == dirName && child.type == WzNodeType::Directory) {
                child.children = std::move(subTree.children);
                break;
            }
        }
    }

    root.childCount = (int)root.children.size();
    return root;
}

// ================================================================
// 단일 WZ 파일 트리 읽기 헬퍼
// ================================================================
WzNode loadSingleWzTree(const std::string& path, const std::vector<uint8_t>& cryptoKey) {
    std::ifstream f(path, std::ios::binary);
    if (!f.is_open()) throw std::runtime_error("파일 열기 실패: " + path);
    WzReader r(f);
    WzHeader h = readHeader(r, path);
    r.seek(h.dataStartPosition);
    if (h.signature == WZ_SIG_PKG2) {
        return readDirTreePkg2(r, cryptoKey);
    }
    return readDirTree(r, h, cryptoKey);
}

// ================================================================
// WZ 폴더 로드 (C# Wz_Structure.LoadWzFolder 동일 로직)
//
// folder 예: "C:\Nexon\Maple\Data\Character"
//   → Character\Character.wz 를 읽고
//   → Character_000.wz, Character_001.wz … 를 merge
// ================================================================
WzNode loadWzFolder(const std::string& folderPath, const std::vector<uint8_t>& cryptoKey) {
    namespace fs = std::filesystem;
    fs::path dir(folderPath);
    std::string name = dir.filename().string();

    // 1. 엔트리 파일 로드
    WzNode root = loadSingleWzTree((dir / (name + ".wz")).string(), cryptoKey);

    // 2. LastWzIndex 확인 (ini 우선, 없으면 파일 스캔)
    int lastIdx = -1;
    fs::path iniPath = dir / (name + ".ini");
    if (fs::exists(iniPath)) {
        std::ifstream ini(iniPath.string());
        std::string line;
        while (std::getline(ini, line)) {
            if (line.rfind("LastWzIndex|", 0) == 0) {
                try { lastIdx = std::stoi(line.substr(12)); } catch (...) {}
                break;
            }
        }
    }
    if (lastIdx < 0) {
        for (int i = 0; ; i++) {
            char buf[8]; std::snprintf(buf, sizeof(buf), "_%03d", i);
            if (!fs::exists(dir / (name + buf + ".wz"))) break;
            lastIdx = i;
        }
    }

    // 3. 분할 파일 merge: Character_000.wz …
    for (int i = 0; i <= lastIdx; i++) {
        char buf[8]; std::snprintf(buf, sizeof(buf), "_%03d", i);
        fs::path extraWz = dir / (name + buf + ".wz");
        if (!fs::exists(extraWz)) continue;
        WzNode extra = loadSingleWzTree(extraWz.string(), cryptoKey);
        for (auto& child : extra.children)
            root.children.push_back(std::move(child));
    }
    root.childCount = (int)root.children.size();
    return root;
}

// ================================================================
// 노드 트리 출력
// ================================================================
void printTree(const WzNode& node, int depth, int maxDepth,
               int& totalNodes, int& totalImages, int& totalDirs) {
    std::string indent(depth * 2, ' ');
    std::string typeTag = (node.type == WzNodeType::Image) ? "[IMG]" : "[DIR]";

    if (depth > 0) {
        std::cout << indent << typeTag << " " << node.name;
        if (!node.children.empty())
            std::cout << "  (" << node.children.size() << " 개 자식)";
        std::cout << "\n";
    }

    if (node.type == WzNodeType::Image) totalImages++;
    else if (depth > 0) totalDirs++;
    totalNodes++;

    if (depth < maxDepth) {
        for (const auto& child : node.children) {
            printTree(child, depth + 1, maxDepth, totalNodes, totalImages, totalDirs);
        }
    }
}

// ================================================================
// TeeStreambuf - std::cout 출력을 파일에도 동시 기록
// ================================================================
class TeeStreambuf : public std::streambuf {
public:
    TeeStreambuf(std::streambuf* console, std::streambuf* file)
        : con(console), fil(file) {}
protected:
    int overflow(int c) override {
        if (c == EOF) return !EOF;
        if (con->sputc((char)c) == EOF) return EOF;
        if (fil->sputc((char)c) == EOF) return EOF;
        return c;
    }
    std::streamsize xsputn(const char* s, std::streamsize n) override {
        con->sputn(s, n);
        return fil->sputn(s, n);
    }
private:
    std::streambuf* con;
    std::streambuf* fil;
};

// ================================================================
// main
// ================================================================
int main(int argc, char* argv[]) {
    std::string wzPath = "C:\\Nexon\\Maple\\Data\\Base\\Base.wz";
    int maxDepth = 3;
    std::string outputPath;

    // ── CLI 인수 파싱: -o <출력파일> ──
    for (int i = 1; i < argc; i++) {
        std::string arg = argv[i];
        if ((arg == "-o" || arg == "--output") && i + 1 < argc) {
            outputPath = argv[++i];
        } else if (arg.rfind("-o", 0) == 0 && arg.size() > 2) {
            outputPath = arg.substr(2); // -o<path> 형태
        }
    }

    // ── 출력 파일 설정 (지정 시 cout 을 파일로도 tee) ──
    std::ofstream outFile;
    std::unique_ptr<TeeStreambuf> teeBuf;
    std::streambuf* origCoutBuf = nullptr;

    if (!outputPath.empty()) {
        outFile.open(outputPath);
        if (!outFile.is_open()) {
            std::cerr << "출력 파일을 열 수 없습니다: " << outputPath << "\n";
            return 1;
        }
        teeBuf = std::make_unique<TeeStreambuf>(std::cout.rdbuf(), outFile.rdbuf());
        origCoutBuf = std::cout.rdbuf(teeBuf.get());
        std::cerr << "출력 파일: " << outputPath << "\n";
    }

    // ── 파일 열기 ──
    std::ifstream file(wzPath, std::ios::binary);
    if (!file.is_open()) {
        std::cerr << "파일을 열 수 없습니다: " << wzPath << "\n";
        return 1;
    }

    file.seekg(0, std::ios::end);
    int64_t fileSize = file.tellg();
    file.seekg(0);

    WzReader reader(file);

    try {
        // ── 헤더 읽기 ──
        std::cout << "=== WZ 파일 분석기 ===\n";
        std::cout << "파일: " << wzPath << "\n";
        std::cout << "크기: " << fileSize << " bytes\n\n";

        WzHeader header = readHeader(reader, wzPath);

        std::cout << "[헤더]\n";
        std::cout << "  시그니처       : " << header.signature << "\n";
        std::cout << "  데이터 크기    : " << header.dataSize << "\n";
        std::cout << "  헤더 크기      : " << header.headerSize << "\n";
        std::cout << "  저작권         : " << header.copyright << "\n";
        std::cout << "  데이터 시작 위치: " << header.dataStartPosition << "\n";
        if (header.signature == WZ_SIG_PKG1)
            std::cout << "  encver 없음     : " << (header.encverMissing ? "예 (KMST1132+)" : "아니오") << "\n";
        std::cout << "\n";

        // ── 키스트림 생성 (2KB 미리 생성) ──
        const size_t KEYSTREAM_SIZE = 4096;
        auto ksKMS = generateKeystream(WZ_IV_KMS, KEYSTREAM_SIZE);
        auto ksGMS = generateKeystream(WZ_IV_GMS, KEYSTREAM_SIZE);

        // ── 암호화 타입 감지 ──
        CryptoType crypto = detectEncryption(reader, header, ksKMS, ksGMS);
        const char* cryptoName[] = { "BMS (암호화 없음)", "KMS", "GMS" };
        std::cout << "[암호화] " << cryptoName[(int)crypto] << "\n\n";

        // 사용할 키스트림 선택
        std::vector<uint8_t> cryptoKey;
        switch (crypto) {
            case CryptoType::KMS: cryptoKey = ksKMS; break;
            case CryptoType::GMS: cryptoKey = ksGMS; break;
            default: break; // BMS = 빈 벡터 (no-op)
        }

        // ── 노드 트리 읽기 ──
        reader.seek(header.dataStartPosition);
        WzNode root = readDirTree(reader, header, cryptoKey);
        root.name = std::filesystem::path(wzPath).filename().string();
        root.type = WzNodeType::Directory;

        // ── KMST1125 포맷 처리 (C# GetDirTree willLoadBaseWz 로직 동일) ──
        //
        // Base.wz 의 DIR 엔트리 (Character, Effect 등) 는 Base.wz 내부에서
        // 비어있음. 실제 IMG 들은 별도 WZ 폴더에 있음:
        //
        //   Data\Base\Base.wz    → DIR 목록만 (Character, Effect, Map …)
        //   Data\Character\      → Character.wz + Character_000.wz … (IMG들)
        //   Data\Effect\         → Effect.wz + Effect_000.wz …
        //
        // C# 흐름: GetDirTree(useBaseWz=true) → 각 빈 DIR 에 대해
        //   wzFolder = parent(baseFolder) / dirName  (= Data\Character)
        //   → LoadWzFolder(wzFolder)
        {
            namespace fs = std::filesystem;
            fs::path wzFs(wzPath);
            fs::path baseDir = wzFs.parent_path();   // Data\Base
            fs::path dataDir = baseDir.parent_path(); // Data

            // 1. 각 빈 DIR → 해당 서브 WZ 폴더 로드
            int loaded = 0;
            for (auto& child : root.children) {
                if (child.type != WzNodeType::Directory || !child.children.empty())
                    continue;
                fs::path subDir = dataDir / child.name;           // Data\Character
                fs::path subWz  = subDir / (child.name + ".wz"); // Data\Character\Character.wz
                if (!fs::exists(subWz)) continue;
                try {
                    WzNode sub = loadWzFolder(subDir.string(), cryptoKey);
                    child.children   = std::move(sub.children);
                    child.childCount = (int)child.children.size();
                    std::cout << "  [로드] " << child.name << ".wz → "
                              << child.children.size() << " 노드\n";
                    loaded++;
                } catch (const std::exception& e) {
                    std::cerr << "  [경고] " << child.name << " 로드 실패: " << e.what() << "\n";
                }
            }
            if (loaded > 0) std::cout << "\n";

            // 2. Base_000.wz … 등 Base 레벨 분할 파일 merge (있을 경우)
            std::string stem = wzFs.stem().string();
            int lastIdx = -1;
            fs::path iniPath = baseDir / (stem + ".ini");
            if (fs::exists(iniPath)) {
                std::ifstream ini(iniPath.string());
                std::string line;
                while (std::getline(ini, line)) {
                    if (line.rfind("LastWzIndex|", 0) == 0) {
                        try { lastIdx = std::stoi(line.substr(12)); } catch (...) {}
                        break;
                    }
                }
            }
            if (lastIdx < 0) {
                for (int i = 0; ; i++) {
                    char buf[8]; std::snprintf(buf, sizeof(buf), "_%03d", i);
                    if (!fs::exists(baseDir / (stem + buf + ".wz"))) break;
                    lastIdx = i;
                }
            }
            for (int i = 0; i <= lastIdx; i++) {
                char buf[8]; std::snprintf(buf, sizeof(buf), "_%03d", i);
                fs::path extraPath = baseDir / (stem + buf + ".wz");
                if (!fs::exists(extraPath)) continue;
                try {
                    WzNode extra = loadSingleWzTree(extraPath.string(), cryptoKey);
                    for (auto& child : extra.children)
                        root.children.push_back(std::move(child));
                    std::cout << "  [병합] " << extraPath.filename().string()
                              << " (" << extra.children.size() << " 노드)\n";
                } catch (const std::exception& e) {
                    std::cerr << "  [경고] " << extraPath.filename() << " 실패: " << e.what() << "\n";
                }
            }
        }

        // ── 출력 ──
        std::cout << "[노드 트리] (최대 깊이: " << maxDepth << ")\n";
        std::cout << "[ROOT] " << root.name
                  << "  (" << root.children.size() << " 개 자식)\n";

        int totalNodes = 0, totalImages = 0, totalDirs = 0;
        printTree(root, 0, maxDepth, totalNodes, totalImages, totalDirs);

        std::cout << "\n[요약]\n";
        std::cout << "  총 노드   : " << totalNodes  << "\n";
        std::cout << "  IMG 파일  : " << totalImages  << "\n";
        std::cout << "  디렉토리  : " << totalDirs    << "\n";

        if (totalNodes > 0)
            std::cout << "\n결과: WZ 파일이 정상적으로 열렸습니다.\n";
        else
            std::cout << "\n경고: 노드가 0개입니다. 파일 형식을 확인하세요.\n";

    } catch (const std::exception& ex) {
        std::cerr << "\n[오류] " << ex.what() << "\n";
        if (origCoutBuf) std::cout.rdbuf(origCoutBuf);
        return 1;
    }

    if (origCoutBuf) {
        std::cout.rdbuf(origCoutBuf);
        std::cerr << "저장 완료: " << outputPath << "\n";
    }

    return 0;
}
