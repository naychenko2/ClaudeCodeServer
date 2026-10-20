import json,glob,os,re,sys
d=json.load(open('/srv/ccs/data/tasks.json'))
done={t['Id']:t for t in d if t['ProjectId']=='fedfe8c3-eaab-48ae-a5bd-1661bb68f7cc' and t['Status']==2 and t.get('ClaudeResult')=='success'}
bysess={t['LinkedSessionId']:k for k,t in done.items() if t.get('LinkedSessionId')}
U=re.compile(r'[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}')
res={}
for f in glob.glob('/srv/ccs/data/claude-profiles/*/projects/*ClaudeCodeServer*/*.jsonl'):
    try:
        with open(f) as fh:
            for i,line in enumerate(fh):
                if i>6: break
                try: o=json.loads(line)
                except: continue
                if o.get('type')!='user': continue
                m=o.get('message',{}).get('content')
                txt=m if isinstance(m,str) else ' '.join(c.get('text','') for c in m if isinstance(c,dict))
                m1=re.search(r'CCS-Session: ([0-9a-f-]{36})',txt); m2=re.search(r'CCS-Task: ([0-9a-f-]{36})',txt)
                ids=[]
                if m2 and m2.group(1) in done: ids=[m2.group(1)]
                elif m1 and m1.group(1) in bysess: ids=[bysess[m1.group(1)]]
                if ids:
                    res.setdefault(ids[0],[]).append((f,os.path.getsize(f)))
                break
    except Exception as e: pass
json.dump(res,open('/tmp/expmeasure/map.json','w'))
print(len(res))
