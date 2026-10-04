import argparse,gzip,json,pathlib,re,struct,sys
sys.stdout.reconfigure(encoding='utf-8')
parser=argparse.ArgumentParser(description='Build the pinned RUAccent stress dictionary')
parser.add_argument('--source',type=pathlib.Path,required=True)
parser.add_argument('--output',type=pathlib.Path,required=True)
args=parser.parse_args()
source=args.source
target=args.output
target.mkdir(parents=True,exist_ok=True)
def load(name):
    with gzip.open(source/(name+'.json.gz'),'rt',encoding='utf-8') as f:return json.load(f)
accents=load('accents');homographs=load('omographs');yo_words=load('yo_words');yo_homographs=load('yo_homographs')
uncertain=set(homographs)|set(yo_homographs)|{'коса'}
vowels=set('аеёиоуыэюя')
lexicon={}
invalid=0
for word,marked in accents.items():
    if word in uncertain or not isinstance(marked,str):continue
    if not re.fullmatch('[а-яё]+',word):continue
    plain=marked.replace('+','').replace('\u0301','')
    if plain.replace('ё','е')!=word.replace('ё','е'):invalid+=1;continue
    marked=re.sub(r'\+([аеёиоуыэюя])',lambda m:m[1]+'\u0301',marked)
    if '+' in marked or marked.count('\u0301')!=1:continue
    if any(marked[i-1] not in vowels for i,c in enumerate(marked) if c=='\u0301'):invalid+=1;continue
    lexicon[word]=marked
for word,restored in yo_words.items():
    if word in uncertain or not re.fullmatch('[а-яё]+',word) or restored.count('ё')!=1:continue
    if restored.replace('ё','е')!=word.replace('ё','е'):continue
    lexicon[word]=restored.replace('ё','ё\u0301')
words=sorted(lexicon)
with (target/'russian-stress.bin').open('wb') as f:
    f.write(b'RST1'+struct.pack('<I',len(words)))
    table=8;data_start=8+len(words)*4
    f.seek(data_start)
    offsets=[]
    for word in words:
        offsets.append(f.tell()-data_start)
        key=word.encode('utf-8');value=lexicon[word].encode('utf-8')
        f.write(struct.pack('<HH',len(key),len(value))+key+value)
    f.seek(table)
    for offset in offsets:f.write(struct.pack('<I',offset))
(target/'russian-stress-ambiguous.txt').write_text('\n'.join(sorted(uncertain))+'\n',encoding='utf-8')
(target/'RUAccent-LICENSE.txt').write_bytes((source/'RUAccent-LICENSE.txt').read_bytes())
revision=(source/'source-revision.txt').read_text().strip()
(target/'来源.md').write_text(f'''# 俄语重音词典来源

RUAccent: https://github.com/Den4ikAI/ruaccent
词典仓库: https://huggingface.co/ruaccent/accentuator
固定版本: `{revision}`
许可: MIT，见同目录 RUAccent-LICENSE.txt。

使用 accents.json.gz、omographs.json.gz、yo_words.json.gz、yo_homographs.json.gz。
仅保留文字与原词对应、且恰有一个有效重音的条目。词典列出的同形异音词、е/ё 歧义词不自动决定读音；保留原文并提示重音待确认。
词典缺失的多音节词不猜测重音。单音节词和单个 ё 的重音位置按已有字母处理。

词条数: {len(words):,}；待消歧词形数: {len(uncertain):,}。
二进制格式 RST1：4 字节标识，uint32 词条数，uint32 相对偏移表，随后各条目为 uint16 键字节数、uint16 值字节数及 UTF-8 键和值。键按 Unicode 序排序。
''',encoding='utf-8')
print({'entries':len(words),'ambiguous':len(uncertain),'invalid_skipped':invalid,'bytes':(target/'russian-stress.bin').stat().st_size})
for word in ['здравствуйте','привет','русский','язык','хочу','выучить','изучать','учиться','замок','мука','все','елка','береза','это']:
    print(word,lexicon.get(word),'ambiguous' if word in uncertain else '')
