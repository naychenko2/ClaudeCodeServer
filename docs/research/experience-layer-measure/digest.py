import json,sys,os
d={t['Id']:t for t in json.load(open('/srv/ccs/data/tasks.json'))}
m=json.load(open('/tmp/expmeasure/map.json'))
def txt(c):
    if isinstance(c,str): return c
    out=[]
    for x in c:
        if isinstance(x,dict):
            if x.get('type')=='text': out.append(x['text'])
            elif x.get('type')=='tool_result':
                out.append(txt(x.get('content') or ''))
    return '\n'.join(out)
order=sorted(m, key=lambda k:(d[k]['ClaudeStartedAt'] or d[k]['CreatedAt']))
idx=[]
for n,k in enumerate(order):
    t=d[k]; f=max(m[k],key=lambda x:x[1])[0]
    ev=[]; first=True
    for line in open(f):
        try:o=json.loads(line)
        except:continue
        ty=o.get('type'); msg=o.get('message') or {}
        if ty=='user':
            c=msg.get('content')
            if first and not (isinstance(c,list) and any(isinstance(x,dict) and x.get('type')=='tool_result' for x in c)):
                first=False; continue
            if isinstance(c,list):
                for x in c:
                    if isinstance(x,dict) and x.get('type')=='tool_result':
                        r=txt(x.get('content') or '')
                        err=x.get('is_error') or any(w in r[:2000] for w in ('error','Error','FAIL','failed','Failed','not found','No such','exception','Exception'))
                        if err: ev.append('  ! '+r[:350].replace('\n',' '))
                    elif isinstance(x,dict) and x.get('type')=='text' and not first:
                        ev.append('USER: '+x['text'][:400].replace('\n',' '))
        elif ty=='assistant':
            for x in msg.get('content') or []:
                if x.get('type')=='text' and x['text'].strip(): ev.append('A: '+x['text'][:350].replace('\n',' '))
                elif x.get('type')=='tool_use':
                    ev.append('T '+x['name']+' '+json.dumps(x.get('input'),ensure_ascii=False)[:220])
    body='\n'.join(ev)
    if len(body)>45000: body=body[:22000]+'\n...[середина опущена]...\n'+body[-22000:]
    head=f"# T{n:03d} id={k[:8]} начало={t['ClaudeStartedAt']} метки={t.get('Labels')}\n## Заголовок\n{t['Title']}\n## Постановка\n{(t.get('Description') or '')[:3500]}\n## Итог (ResultMarkdown)\n{(t.get('ResultMarkdown') or '')[:2500]}\n## Ход работы (T=инструмент, !=ошибка, A=реплика)\n"
    open(f'/tmp/expmeasure/dig/T{n:03d}.md','w').write(head+body)
    idx.append({'n':n,'id':k,'title':t['Title'],'start':t['ClaudeStartedAt'],'chars':len(head+body),'events':len(ev)})
json.dump(idx,open('/tmp/expmeasure/index.json','w'),ensure_ascii=False,indent=0)
print(len(idx), sum(i['chars'] for i in idx)//1000,'K chars')
