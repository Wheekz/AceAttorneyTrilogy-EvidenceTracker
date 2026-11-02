import struct,re,sys,collections,glob,os,unicodedata,json,io
sys.stdout=io.TextIOWrapper(sys.stdout.buffer,encoding='utf-8')
LANGS=['','_u','_f','_g','_s','_t','_b','_l','_k']
VOTERS=LANGS[:-1]   # le coreen, decoupe en syllabes, ne vote pas
LIMIT=5; MAJ=5
src=open('decomp/MessageSystem.cs',encoding='utf-8').read()
args=[int(x) for x in re.findall(r'\d+',re.search(r'code_proc_arg_count_table = new ushort\[129\]\s*\{(.*?)\}',src,re.S).group(1))]
pice=open('decomp/piceDataCtrl.cs',encoding='utf-8').read()
def load(p):
    b=open(p,'rb').read();n=struct.unpack_from('<H',b,0)[0];off=struct.unpack_from('<%dI'%n,b,4);h=4+n*4
    d=struct.unpack_from('<%dH'%((len(b)-h)//2),b,h);return off,d,h
SINGLE=re.compile(r'[\u3040-\u30ff\u3400-\u9fff\uf900-\ufaff\uac00-\ud7a3]')
def toks(s):
    r=[]
    for w in re.findall(r"[^\W_]+(?:['’\-―][^\W_]+)*",s):
        cur=''
        for ch in w:
            if SINGLE.match(ch):
                if cur:r.append(cur);cur=''
                r.append(ch)
            else:cur+=ch
        if cur:r.append(cur)
    return r
FILES={}
def mdt(g,stem,l):
    k=(g,stem,l)
    if k not in FILES:
        p='mdt_all/%s/%s_text%s.mdt'%(g,stem,l)
        FILES[k]=load(p) if os.path.exists(p) else None
    return FILES[k]
TOK={}
def tokens(g,stem,l,no):
    k=(g,stem,l,no)
    if k in TOK:return TOK[k]
    m=mdt(g,stem,l);r=[]
    if m:
        off,d,h=m;i=no-128
        if 0<=i<len(off):
            a=(off[i]-h)//2;e=(off[i+1]-h)//2 if i+1<len(off) else len(d)
            e=min(e,len(d));out=[];j=a
            while j<e:
                c=d[j]
                if c<128:out.append(' ');j+=1+args[c]
                else:out.append(chr(c-128));j+=1
            r=toks(unicodedata.normalize('NFKC',''.join(out)).lower())
    TOK[k]=r;return r
def newcount(a,b):
    if a==b:return 0
    ca=collections.Counter(a)
    if sum((collections.Counter(b)-ca).values())>LIMIT:return 99
    prev=[0]*(len(b)+1)
    for x in a:
        cur=[0]
        for j,y in enumerate(b):
            cur.append(prev[j]+1 if x==y else max(prev[j+1],cur[j]))
        prev=cur
    return len(b)-prev[-1]
# alignement des numeros de messages entre langues
mis=[];stems=collections.defaultdict(set)
for f in glob.glob('mdt_all/*/sc*_text*.mdt'):
    g=os.path.normpath(f).split(os.sep)[1];m=re.match(r'(sc[^/]*?)_text(_\w)?\.mdt$',os.path.basename(f))
    stems[(g,m.group(1))].add(m.group(2) or '')
bad=set()
for (g,s),ls in sorted(stems.items()):
    cnt={l:len(mdt(g,s,l)[0]) for l in ls}
    if len(set(cnt.values()))>1 or len(ls)<9: mis.append((g,s,cnt));bad.add((g,s))
print('fichiers',len(stems),'non alignes',len(mis))
for x in mis[:20]:print(' ',x)
edges=set();stats=collections.Counter();pairs_all=[]
blocks={'GS1':'gs1_note_data_','GS2':'gs2_note_data_','GS3':'gs3_note_data_'}
for g,f in (('GS1','scenario.cs'),('GS2','scenario_GS2.cs'),('GS3','scenario_GS3.cs')):
    t=open('decomp/'+f,encoding='utf-8').read()
    c={k:int(v) for k,v in re.findall(r'public static uint (\w+) = (\d+)u;',t)}
    def val(x):
        x=x.strip();return int(x[:-1]) if x.endswith('u') else c.get(x)
    s=pice.split(blocks[g]+' = new List<piceData>')[1]
    s=s[:s.index('};')]
    es=[[x.strip() for x in e.split(',')] for e in re.findall(r'new piceData\(([^;]*?)\)\s*[,\n]',s)]
    fam={i:(a[6],a[5],a[2]) for i,a in enumerate(es)}
    nodes=collections.defaultdict(set)
    for nm,cs,day,sub in re.findall(r'SHOW_DATA\[\] (Sce(\d+)_(\d+)(?:_(\d+))?_show_data) = new',t):
        m=re.search(nm+r' = new SHOW_DATA\[\d+\]\s*\{(.*?)\n\t\};',t,re.S)
        base='sc%s_%s%s'%(cs,day,('_'+sub) if sub else '')
        parts=[st for (gg,st) in stems if gg==g and re.fullmatch(re.escape(base)+r'[a-z]?',st) and (gg,st) not in bad]
        for e in re.findall(r'new SHOW_DATA\(([^)]*)\)',m.group(1)):
            room,seq,item,pl,end,mt,mf=[val(a) for a in e.split(',')]
            if pl==255 or item==255:continue
            kind=end if g!='GS1' else 0
            for st in parts:
                nodes[(cs,pl,kind,fam.get(item,item))].add((st,mt,day+'_'+(sub or '')))
    for k,ns in nodes.items():
        ns=sorted(ns)
        for a in ns:
            for b in ns:
                if a[:2]==b[:2]:continue
                v=0;av=0
                for l in VOTERS:
                    x,y=tokens(g,a[0],l,a[1]),tokens(g,b[0],l,b[1])
                    if not x or not y:continue
                    av+=1
                    if newcount(x,y)<=LIMIT:v+=1
                if av>=MAJ and v>=MAJ:
                    edges.add((g,a[0],a[1],b[0],b[1]))
print('liens',len(edges))
os.makedirs('out',exist_ok=True)
by=collections.defaultdict(list)
for g,s1,m1,s2,m2 in sorted(edges): by[g].append('%s:%d>%s:%d'%(s1,m1,s2,m2))
with open('out/ReplyCoverage.cs','w',encoding='utf-8',newline='\n') as o:
    o.write('// Généré par tools/generate_coverage.py : ne pas modifier à la main.\n// Identifiants uniquement, aucun texte du jeu.\n// "fichierA:msgA>fichierB:msgB" : qui a lu A n\'apprend rien de neuf en lisant B.\nnamespace PWAAT_QoL\n{\n    internal static class ReplyCoverageData\n    {\n')
    for g in ('GS1','GS2','GS3'):
        o.write('        internal const string %s =\n'%g)
        L=by[g]
        for i in range(0,len(L),6):
            o.write('            "%s;"%s\n'%(';'.join(L[i:i+6]),' +' if i+6<len(L) else ';'))
        if not L:o.write('            "";\n')
    o.write('    }\n}\n')
print({g:len(v) for g,v in by.items()},os.path.getsize('out/ReplyCoverage.cs'))
json.dump(sorted(edges),open('edges.json','w'))
