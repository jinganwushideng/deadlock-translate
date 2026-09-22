"""Ship a readable first-run guide in Markdown and UTF-8-BOM plain text."""
import argparse,pathlib,re,shutil
parser=argparse.ArgumentParser();parser.add_argument('--output',required=True);args=parser.parse_args()
root=pathlib.Path(__file__).resolve().parents[1];output=pathlib.Path(args.output)
guide=(root/'docs/USER-GUIDE.md').read_text(encoding='utf-8-sig')
output.mkdir(parents=True,exist_ok=True)
for name in ('LICENSE','NOTICE','THIRD-PARTY-NOTICES.md'):
    shutil.copy2(root/name,output/name)
shutil.copytree(root/'LICENSES',output/'LICENSES',dirs_exist_ok=True)
(output/'USER-GUIDE.md').write_text(guide,encoding='utf-8')
plain=re.sub(r'^```[^\n]*\n','',guide,flags=re.M)
plain=re.sub(r'^#{1,6}\s+','',plain,flags=re.M).replace('**','').replace('`','')
(output/'使用说明-先读我.txt').write_text(plain,encoding='utf-8-sig')
(output/'Steam-Launch-Options.txt').write_text('Steam → 库 → 右键 Deadlock → 属性 → 通用 → 启动选项\n\n格式示例（必须把引号内路径改成你自己的 EXE 完整路径）：\n"D:\\DeadlockTranslate\\DeadlockTranslate.exe" --steam-game %command%\n\n不要照抄作者电脑的 G 盘路径。保留英文双引号和 --steam-game %command%。已有普通游戏参数放在末尾；已有其他 %command% 启动器时请先协商合并。\n\n完整安装、配置与卸载步骤见“使用说明-先读我.txt”。\n',encoding='utf-8-sig')
print('Packaged Chinese usage guide and portable Steam launch example.')
