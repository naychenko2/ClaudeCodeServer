# Реактивная доставка: запрос = текст ошибки инструмента (+ предшествующий вызов), BM25 по пулу, топ-3, порог.
import json,glob,re,math,collections
W=re.compile(r'[a-zа-яё0-9_.\-/]{3,}',re.I)
def tok(s): return [w.lower()[:7] for w in W.findall(s)]
def bm25(q,docs,k1=1.2,b=0.75):
    N=len(docs); dl=[len(x) for x in docs]; avg=sum(dl)/N
    df=collections.Counter(w for x in docs for w in set(x)); out=[]
    for i,x in enumerate(docs):
        tf=collections.Counter(x); s=0
        for w in set(q):
            if w in tf: s+=math.log(1+(N-df[w]+.5)/(df[w]+.5))*tf[w]*(k1+1)/(tf[w]+k1*(1-b+b*dl[i]/avg))
        out.append(s)
    return out
R=[];[R.extend(json.load(open(f))) for f in sorted(glob.glob('/tmp/expmeasure/judge/J*.json'))]
data=[]
for r in R:
    n=int(r['target'][1:]); pool=json.load(open(f'/tmp/expmeasure/pool/T{n:03d}.json'))
    docs=[tok(e['task']+' '+' '.join(e.get('search_keys',[]))+' '+e['solution']) for e in pool]
    lines=open(f'/tmp/expmeasure/dig/T{n:03d}.md').read().split('\n')
    u={x['id']:x['est_saved_calls'] for x in r['useful']}
    for i,l in enumerate(lines):
        if l.startswith('  ! '):
            prev=next((lines[j] for j in range(i-1,max(i-4,0),-1) if lines[j].startswith('T ')),'')
            sc=bm25(tok(prev[:300]+' '+l),docs); top=sorted(range(len(pool)),key=lambda k:-sc[k])[:3]
            for k in top: data.append((r['target'],pool[k]['id'],sc[k],pool[k]['id'] in u,u.get(pool[k]['id'],0)))
for th in (0,10,15,20,25,30,40):
    shown=[x for x in data if x[2]>th]; hit={(t,i):s for t,i,_,ok,s in shown if ok}
    print(f'порог {th}: подсказок {len(shown)}, полезных {len(hit)}, экономия {sum(hit.values())}, точность {len([x for x in shown if x[3]])/max(1,len(shown)):.1%}')
