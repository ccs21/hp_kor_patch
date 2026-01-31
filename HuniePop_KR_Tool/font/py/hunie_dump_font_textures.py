# hunie_dump_font_textures.py
import argparse, os, re, json
import UnityPy

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--assets", required=True)
    ap.add_argument("--dep", action="append", default=[])
    ap.add_argument("--out", required=True)
    ap.add_argument("--name-regex", default=r"^font_exo_")
    args = ap.parse_args()

    os.makedirs(args.out, exist_ok=True)
    tex_re = re.compile(args.name_regex, re.IGNORECASE)

    env = UnityPy.Environment()
    env.load_file(args.assets)
    for d in args.dep:
        env.load_file(d)

    manifest = []
    for obj in env.objects:
        if obj.type.name != "Texture2D":
            continue
        tt = obj.read_typetree()
        name = tt.get("m_Name","")
        if not tex_re.search(name):
            continue

        # 이미지 덤프 (UnityPy가 decode 가능한 포맷이면 자동으로 PNG 저장됨)
        try:
            data = obj.read()
            img = data.image
            out_path = os.path.join(args.out, f"{name}.png")
            img.save(out_path)
            w, h = img.size
            manifest.append({"name": name, "path_id": int(obj.path_id), "w": w, "h": h, "out": out_path})
            print("[OK]", name, w, h)
        except Exception as e:
            manifest.append({"name": name, "path_id": int(obj.path_id), "error": str(e)})
            print("[FAIL]", name, e)

    with open(os.path.join(args.out, "font_textures_manifest.json"), "w", encoding="utf-8") as f:
        json.dump(manifest, f, ensure_ascii=False, indent=2)

if __name__ == "__main__":
    main()
