// Text rewrites of translated MSL, and what they read in it: pure functions of the text, which need no device and no
// driver state (the driver's tests call them on their own). Each gives the text back unchanged where what it looks for
// isn't there, or isn't of the shape it expects. Who calls them: pipelines/compile.mm (uniform structs in place, early
// fragment tests, a shader override's specialization values), pipelines/rtpipeline.mm (intersectors),
// pipelines/pipelines.mm (the records tessellation's kernels exchange).
#include "shaders/mslpatch.h"
#include <algorithm>
#include <cctype>
#include <cstring>
#include <regex>
#include <set>
#include <utility>

namespace {

bool isIdent(char c) { return isalnum((unsigned char)c) || c == '_'; }

// Whether T's block twin is a member of a struct that is read as "constant S&" and never as "device S&" (a storage
// buffer's struct would need device references instead), or of another struct's twin that is
bool inConstantBlock(const std::string& msl, const std::string& t) {
    bool constant = false;
    const std::string member = "    " + t + "_1 ";
    for (size_t at = 0; (at = msl.find(member, at)) != std::string::npos; at += member.size()) {
        size_t open = msl.rfind("\nstruct ", at);
        if (open == std::string::npos) continue;
        size_t b = open + 8, e = msl.find('\n', b);
        std::string s = msl.substr(b, e - b);
        if (msl.find("device " + s + "&") != std::string::npos) return false;
        if (msl.find("constant " + s + "&") != std::string::npos) constant = true;
        // Nested: a member of another block twin
        else if (s.size() > 2 && s.compare(s.size() - 2, 2, "_1") == 0 && s != t + "_1" &&
                 msl.find("\nstruct " + s.substr(0, s.size() - 2) + "\n") != std::string::npos &&
                 inConstantBlock(msl, s.substr(0, s.size() - 2)))
            constant = true;
    }
    return constant;
}

size_t countOf(const std::string& text, const char* what) {
    size_t n = 0, len = strlen(what);
    for (size_t at = 0; (at = text.find(what, at)) != std::string::npos; at += len) n++;
    return n;
}

// The first use of identifier q followed by what, from at: not part of a longer name or a member access
size_t findUse(const std::string& s, const std::string& q, const std::string& what, size_t at) {
    for (; (at = s.find(q + what, at)) != std::string::npos; at++)
        if (at == 0 || (!isIdent(s[at - 1]) && s[at - 1] != '.' && s[at - 1] != '>')) return at;
    return std::string::npos;
}

// The arguments of the call whose '(' is at open, split at top-level commas; *close gets the matching ')'
bool callArgs(const std::string& s, size_t open, std::vector<std::string>* args, size_t* close) {
    int depth = 0;
    size_t start = open + 1;
    for (size_t i = open; i < s.size(); i++) {
        char c = s[i];
        if (c == '(' || c == '[' || c == '{') depth++;
        else if (c == ')' || c == ']' || c == '}') {
            if (--depth == 0) {
                args->push_back(s.substr(start, i - start));
                *close = i;
                return true;
            }
        } else if (c == ',' && depth == 1) {
            args->push_back(s.substr(start, i - start));
            start = i + 1;
            while (start < s.size() && s[start] == ' ') start++;
        }
    }
    return false;
}

}  // namespace

// ---------------------------------------------------------------------------------------------------------------
// Uniform structs read in place (KSA_MTL_IN_PLACE_SHADER_PARAMETERS, on by default). GLSL passes structs by value,
// and SPIRV-Cross gives every helper that takes a uniform block's struct a copy in a local: "T _N; _N.a = ubo.s.a;
// ..." field by field, or "T param = p;", at each call, inside raymarch loops too. KSA's trail parameters are 1.2 KB
// with their arrays, and a copy whose arrays a helper indexes lives on the stack. Here the copies become references
// to the block (its std140 twin T_1 has the same fields) and the helpers take "constant T_1&". Only copies of an
// expression rooted in a name declared "constant X& name" are touched; anything this makes invalid (a copy that is
// written to, a helper also called with a local struct) fails to compile, and kmtlCompileStageMSL then compiles the
// translation as it was.
std::string kmtlMslUniformRefs(const std::string& msl) {
    // Structs with a block-layout twin
    std::vector<std::string> twins;
    for (size_t at = 0; (at = msl.find("\nstruct ", at)) != std::string::npos; at += 8) {
        size_t b = at + 8, e = b;
        while (e < msl.size() && isIdent(msl[e])) e++;
        if (e - b > 2 && msl.compare(e - 2, 2, "_1") == 0 && msl[e] == '\n') {
            std::string t = msl.substr(b, e - b - 2);
            if (msl.find("\nstruct " + t + "\n") != std::string::npos && inConstantBlock(msl, t)) twins.push_back(t);
        }
    }
    if (twins.empty()) return msl;
    std::string out = msl;
    for (const std::string& t : twins) {
        const std::string from = "thread const " + t + "& ", to = "constant " + t + "_1& ";
        for (size_t at = 0; (at = out.find(from, at)) != std::string::npos; at += to.size()) out.replace(at, from.size(), to);
    }
    // Names declared "constant X& name"
    std::set<std::string> roots;
    for (size_t at = 0; (at = out.find("constant ", at)) != std::string::npos; at += 9) {
        if (at > 0 && isIdent(out[at - 1])) continue;
        size_t amp = out.find("& ", at);
        if (amp == std::string::npos || out.find_first_of(",;(){}\n", at) < amp) continue;
        size_t b = amp + 2, e = b;
        while (e < out.size() && isIdent(out[e])) e++;
        if (e > b) roots.insert(out.substr(b, e - b));
    }
    auto rooted = [&](const std::string& expr) {
        size_t b = expr.compare(0, 2, "(*") == 0 ? 2 : 0, e = b;
        while (e < expr.size() && isIdent(expr[e])) e++;
        if (e == b || !roots.count(expr.substr(b, e - b))) return false;
        for (char c : expr) if (!isIdent(c) && !strchr(".[]()*", c)) return false;
        return true;
    };
    auto split = [](const std::string& text) {
        std::vector<std::string> lines;
        for (size_t b = 0; b <= text.size();) {
            size_t e = text.find('\n', b);
            if (e == std::string::npos) e = text.size();
            lines.push_back(text.substr(b, e - b));
            b = e + 1;
        }
        return lines;
    };
    auto join = [](const std::vector<std::string>& lines) {
        std::string text;
        for (size_t i = 0; i < lines.size(); i++) { text += lines[i]; if (i + 1 < lines.size()) text += '\n'; }
        return text;
    };
    // The struct a line declares: "<indent>T name..." for a twin T, its name's end in *nameEnd
    auto declares = [&](const std::string& l, std::string* name, size_t* nameEnd) -> const std::string* {
        size_t ind = l.find_first_not_of(' ');
        if (ind == std::string::npos) return nullptr;
        for (const std::string& t : twins) {
            if (l.compare(ind, t.size() + 1, t + " ") != 0) continue;
            size_t nb = ind + t.size() + 1, ne = nb;
            while (ne < l.size() && isIdent(l[ne])) ne++;
            if (ne == nb) return nullptr;
            *name = l.substr(nb, ne - nb);
            *nameEnd = ne;
            return &t;
        }
        return nullptr;
    };
    // Pass 1: "T _N;" then "_N.f = SRC.f;" for every field: _N becomes SRC
    std::vector<std::string> lines = split(out), kept;
    std::vector<std::pair<std::string, std::string>> renames;
    for (size_t i = 0; i < lines.size(); i++) {
        const std::string& l = lines[i];
        std::string name;
        size_t ne;
        if (declares(l, &name, &ne) && ne == l.size() - 1 && l[ne] == ';') {
            std::string src;
            size_t j = i + 1;
            for (; j < lines.size(); j++) {
                const std::string& f = lines[j];
                size_t fi = f.find_first_not_of(' ');
                if (fi == std::string::npos || f.compare(fi, name.size() + 1, name + ".") != 0) break;
                size_t eq = f.find(" = ", fi);
                if (eq == std::string::npos || f.back() != ';') break;
                std::string field = f.substr(fi + name.size(), eq - fi - name.size());  // ".f" or ".f[2]"
                std::string rhs = f.substr(eq + 3, f.size() - eq - 4);
                if (rhs.size() <= field.size() || rhs.compare(rhs.size() - field.size(), field.size(), field) != 0) break;
                std::string s2 = rhs.substr(0, rhs.size() - field.size());
                if (!src.empty() && s2 != src) break;
                src = s2;
            }
            if (!src.empty() && j > i + 1 && rooted(src)) {
                renames.emplace_back(name, src);
                i = j - 1;
                continue;
            }
        }
        kept.push_back(l);
    }
    std::string joined = join(kept);
    for (auto& [name, src] : renames) {
        std::string r;
        r.reserve(joined.size());
        for (size_t at = 0;;) {
            size_t f = joined.find(name, at);
            if (f == std::string::npos) { r.append(joined, at, std::string::npos); break; }
            bool whole = (f == 0 || !isIdent(joined[f - 1])) && (f + name.size() >= joined.size() || !isIdent(joined[f + name.size()]));
            r.append(joined, at, f - at);
            r += whole ? src : name;
            at = f + name.size();
        }
        joined.swap(r);
    }
    // Pass 2: "T x = SRC;" becomes "constant T_1& x = SRC;"
    lines = split(joined);
    bool changed = !renames.empty() || out != msl;
    for (std::string& l : lines) {
        std::string name;
        size_t ne;
        const std::string* t = declares(l, &name, &ne);
        if (!t || l.compare(ne, 3, " = ") != 0 || l.back() != ';') continue;
        std::string src = l.substr(ne + 3, l.size() - ne - 4);
        if (!rooted(src)) continue;
        l = l.substr(0, l.find_first_not_of(' ')) + "constant " + *t + "_1& " + name + " = " + src + ";";
        roots.insert(name);
        changed = true;
    }
    return changed ? join(lines) : msl;
}

// ---------------------------------------------------------------------------------------------------------------
// Early fragment tests (pipelines/compile.mm): whether a fragment shader's every store is to the selection mask, and
// the stage with the attribute

bool kmtlMslStoresOnlySelection(const std::string& msl) {
    size_t stores = countOf(msl, ".write(");
    // Writable buffers: a "device " that isn't "const device ", but for spvUnsafeArray's own accessor
    for (size_t at = 0; (at = msl.find("device ", at)) != std::string::npos; at += 7) {
        bool constant = at >= 6 && msl.compare(at - 6, 6, "const ") == 0;
        if (!constant && msl.compare(at + 7, 14, "T& operator []") != 0) return false;
    }
    return msl.find("\nfragment ") != std::string::npos && stores > 0 && stores == countOf(msl, "selectedMap.write(") &&
           msl.find("atomic_") == std::string::npos && msl.find("discard_fragment") == std::string::npos &&
           msl.find("[[depth(") == std::string::npos && msl.find("[[sample_mask]]") == std::string::npos &&
           msl.find("[[stencil]]") == std::string::npos && msl.find("early_fragment_tests") == std::string::npos;
}

// A fragment stage's MSL with early fragment tests ("" without a fragment function)
std::string kmtlMslWithEarlyTests(std::string msl) {
    size_t at = msl.find("\nfragment ");
    if (at == std::string::npos) return "";
    msl.insert(at + 1, "[[early_fragment_tests]] ");
    return msl;
}

// ---------------------------------------------------------------------------------------------------------------
// Shader overrides (pipelines/compile.mm)

// A replacement's MSL with the specialization values of a translation. A translation bakes them in as function constant
// defaults ("constant T x = is_function_constant_defined(x_tmp) ? x_tmp : <value>;"): each such line of the
// translation replaces the replacement's line that declares the same constant
std::string kmtlMslKeepSpecialization(std::string msl, const std::string& translated) {
    for (size_t at = 0; (at = translated.find("is_function_constant_defined(", at)) != std::string::npos; at++) {
        size_t ls = translated.rfind('\n', at) + 1, le = translated.find('\n', at);
        size_t eq = translated.find(" = ", ls);
        if (eq == std::string::npos || eq > at) continue;
        std::string head = translated.substr(ls, eq - ls), line = translated.substr(ls, le - ls);
        size_t os = msl.find(head + " = ");
        if (os != std::string::npos) msl.replace(os, msl.find('\n', os) - os, line);
    }
    return msl;
}

// ---------------------------------------------------------------------------------------------------------------
// Traces as intersector calls (KSA_MTL_IVA_RAY_SEARCH, on by default)
//
// SPIRV-Cross writes each trace of the rewritten kernel as an intersection_query: reset, one next(), then the
// committed hit's getters. With opaque geometry (the precondition of the whole rewrite: KSA's instances are opaque)
// next() never stops at a candidate, so this is a closest-hit (or, with accept-any, any-hit) query, which Metal's
// intersector answers in one intersect() call that it traverses faster: the ray tracing kernel ~15 % faster, the same
// image. A site becomes
//
//   float3 _ksaRayN_o = <origin>; float3 _ksaRayN_d = <direction>;
//   intersector<...> _ksaRayN_x; ksaConfigIntersector(_ksaRayN_x, <ray flags>);
//   intersection_result<...> _ksaRayN = _ksaRayN_x.intersect(ray(_ksaRayN_o, _ksaRayN_d, <tmin>, <tmax>), <as>, <mask>);
//
// and the getters after it in the same function, up to the query's next reset, read _ksaRayN. Any other use of a
// rewritten query, or a trace of another shape, leaves the kernel as translated.

std::string kmtlMslRtIntersectors(const std::string& msl) {
    static const char* kTags = "raytracing::instancing, raytracing::triangle_data, raytracing::world_space_data";
    static const char* kHelper = R"(
template <typename X> static inline __attribute__((always_inline)) void ksaConfigIntersector(thread X& x, uint flags)
{
    if ((flags & 1) != 0) x.force_opacity(forced_opacity::opaque);
    if ((flags & 2) != 0) x.force_opacity(forced_opacity::non_opaque);
    if ((flags & 4) != 0) x.accept_any_intersection(true);
    if ((flags & 16) != 0) x.set_triangle_cull_mode(triangle_cull_mode::back);
    if ((flags & 32) != 0) x.set_triangle_cull_mode(triangle_cull_mode::front);
    if ((flags & 64) != 0) x.set_opacity_cull_mode(opacity_cull_mode::opaque);
    if ((flags & 128) != 0) x.set_opacity_cull_mode(opacity_cull_mode::non_opaque);
    if ((flags & 256) != 0) x.set_geometry_cull_mode(geometry_cull_mode::triangle);
    if ((flags & 512) != 0) x.set_geometry_cull_mode(geometry_cull_mode::bounding_box);
    x.assume_geometry_type(raytracing::geometry_type::triangle);
}
)";
    const std::string helperAfter = "intersection_params spvMakeIntersectionParams(uint flags)\n{";
    size_t helperAt = msl.find(helperAfter);
    if (helperAt == std::string::npos || (helperAt = msl.find("\n}\n", helperAt)) == std::string::npos) return msl;
    std::string out = msl.substr(0, helperAt + 3) + kHelper + msl.substr(helperAt + 3);

    const std::string reset = ".reset(ray(";
    int site = 0;
    std::set<std::string> queries;
    for (size_t at = 0; (at = out.find(reset, at)) != std::string::npos;) {
        size_t qb = at;
        while (qb > 0 && isIdent(out[qb - 1])) qb--;
        const std::string q = out.substr(qb, at - qb);
        if (qb > 0 && (out[qb - 1] == '.' || out[qb - 1] == '>')) return msl;
        std::vector<std::string> args, rayArgs, flags;
        size_t close, rayClose, flagsClose;
        if (q.empty() || !callArgs(out, at + 6, &args, &close) || args.size() != 4 ||
            !callArgs(out, at + 10, &rayArgs, &rayClose) || rayArgs.size() != 4 ||
            args[3].compare(0, 26, "spvMakeIntersectionParams(") != 0 ||
            !callArgs(args[3], 25, &flags, &flagsClose) || flags.size() != 1 || flagsClose != args[3].size() - 1 ||
            out.compare(close, 2, ");") != 0)
            return msl;
        // The next two statements: "bool _a = q.next();" and "uint _b = uint(q.get_committed_intersection_type());"
        size_t lineEnd = out.find('\n', close);
        size_t nextEnd = out.find('\n', lineEnd + 1), typeEnd = out.find('\n', nextEnd + 1);
        if (typeEnd == std::string::npos) return msl;
        std::string nextLine = out.substr(lineEnd + 1, nextEnd - lineEnd - 1);
        std::string typeLine = out.substr(nextEnd + 1, typeEnd - nextEnd - 1);
        size_t ni = nextLine.find_first_not_of(' '), ti = typeLine.find_first_not_of(' ');
        const std::string nextCall = " = " + q + ".next();", typeCall = " = uint(" + q + ".get_committed_intersection_type());";
        if (ni == std::string::npos || ti == std::string::npos || nextLine.compare(ni, 5, "bool ") != 0 ||
            nextLine.size() < nextCall.size() || nextLine.compare(nextLine.size() - nextCall.size(), nextCall.size(), nextCall) != 0 ||
            typeLine.compare(ti, 5, "uint ") != 0 || typeLine.size() < typeCall.size() ||
            typeLine.compare(typeLine.size() - typeCall.size(), typeCall.size(), typeCall) != 0)
            return msl;
        std::string indent = out.substr(lineEnd + 1, ni);
        std::string nextVar = nextLine.substr(ni + 5, nextLine.size() - nextCall.size() - ni - 5);
        std::string typeVar = typeLine.substr(ti + 5, typeLine.size() - typeCall.size() - ti - 5);
        std::string r = "_ksaRay" + std::to_string(site++);
        std::string code = "float3 " + r + "_o = " + rayArgs[0] + "; float3 " + r + "_d = " + rayArgs[1] + ";\n" +
            indent + "raytracing::intersector<" + kTags + "> " + r + "_x;\n" +
            indent + "ksaConfigIntersector(" + r + "_x, " + flags[0] + ");\n" +
            indent + "raytracing::intersection_result<" + kTags + "> " + r + " = " + r + "_x.intersect(ray(" + r + "_o, " +
            r + "_d, " + rayArgs[2] + ", " + rayArgs[3] + "), " + args[1] + ", " + args[2] + ");\n" +
            // With opaque geometry next() returns false: the query never stops at a candidate
            indent + "bool " + nextVar + " = false;\n" +
            indent + "uint " + typeVar + " = uint(" + r + ".type);";
        // The getters up to the query's next reset or the function's end, whichever comes first
        size_t end = std::min(findUse(out, q, reset, typeEnd), out.find("\n}\n", typeEnd));
        if (end == std::string::npos) end = out.size();
        std::string body = out.substr(typeEnd, end - typeEnd);
        const std::pair<std::string, std::string> getters[] = {
            {".get_committed_primitive_id()", "int(" + r + ".primitive_id)"},
            {".get_committed_distance()", r + ".distance"},
            {".get_committed_user_instance_id()", "int(" + r + ".user_instance_id)"},
            {".get_committed_world_to_object_transform()", r + ".world_to_object_transform"},
            {".get_committed_triangle_barycentric_coord()", r + ".triangle_barycentric_coord"},
            {".get_world_space_ray_origin()", r + "_o"},
            {".get_world_space_ray_direction()", r + "_d"},
        };
        for (const auto& [getter, field] : getters)
            for (size_t g; (g = findUse(body, q, getter, 0)) != std::string::npos;) body.replace(g, q.size() + getter.size(), field);
        if (findUse(body, q, ".", 0) != std::string::npos) return msl;
        queries.insert(q);
        out = out.substr(0, qb) + code + body + out.substr(end);
        at = qb + code.size() + body.size();
    }
    // A rewritten query read anywhere else (a getter in another function, before its first reset) would read a query
    // that is no longer run
    for (const std::string& q : queries)
        if (findUse(out, q, ".", 0) != std::string::npos) return msl;
    return site ? out : msl;
}

// ---------------------------------------------------------------------------------------------------------------
// Tessellation (pipelines/pipelines.mm)

// The field types (and names) of a struct in translated MSL: how the records two kernels exchange are compared
std::vector<std::string> kmtlMslStructFields(const std::string& msl, const std::string& name, std::vector<std::string>* names) {
    std::vector<std::string> types;
    size_t p = msl.find("struct " + name + "\n{");
    if (p == std::string::npos) return types;
    size_t e = msl.find("};", p);
    std::regex field(R"(^\s+([^;]+?)\s+(\w+)(\s*\[\[[^\]]*\]\])?;\s*$)");
    std::string body = msl.substr(p, e - p);
    size_t pos = 0;
    while (pos < body.size()) {
        size_t nl = body.find('\n', pos);
        std::string line = body.substr(pos, nl == std::string::npos ? std::string::npos : nl - pos);
        std::smatch m;
        if (std::regex_match(line, m, field)) { types.push_back(m[1]); names->push_back(m[2]); }
        if (nl == std::string::npos) break;
        pos = nl + 1;
    }
    return types;
}
