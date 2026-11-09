import json,re,math,glob,collections
d={t['Id']:t for t in json.load(open('/srv/ccs/data/tasks.json'))}
idx=json.load(open('/tmp/expmeasure/index.json'))
W=re.compile(r'[a-zа-яё0-9_.\-/]{3,}',re.I)
def tok(s): return [w.lower()[:7] for w in W.findall(s)]
def bm25(query,docs,k1=1.2,b=0.75):
    N=len(docs); dl=[len(x) for x in docs]; avg=sum(dl)/N
    df=collections.Counter(w for x in docs for w in set(x))
    q=set(query); out=[]
    for i,x in enumerate(docs):
        tf=collections.Counter(x); s=0
        for w in q:
            if w in tf:
                idf=math.log(1+(N-df[w]+.5)/(df[w]+.5))
                s+=idf*tf[w]*(k1+1)/(tf[w]+k1*(1-b+b*dl[i]/avg))
        out.append(s)
    return out
res=[]
for f in sorted(glob.glob('/tmp/expmeasure/judge/J*.json')):
    for r in json.load(open(f)):
        n=int(r['target'][1:]); t=d[idx[n]['id']]
        pool=json.load(open(f'/tmp/expmeasure/pool/T{n:03d}.json'))
        docs=[tok(e['task']+' '+' '.join(e.get('search_keys',[]))+' '+e['solution']) for e in pool]
        sc=bm25(tok(t['Title']+' '+(t.get('Description') or '')[:2000]),docs)
        rank={pool[i]['id']:k+1 for k,i in enumerate(sorted(range(len(pool)),key=lambda i:-sc[i]))}
        for u in r.get('useful',[]):
            res.append((r['target'],u['id'],rank.get(u['id']),u.get('est_saved_calls',0)))
json.dump(res,open('/tmp/expmeasure/retrieval.json','w'))
for x in res: print(x)
