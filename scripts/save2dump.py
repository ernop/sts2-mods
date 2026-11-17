import json,sys
d=json.load(open(sys.argv[1]));m=d['acts'][int(sys.argv[2]) if len(sys.argv)>2 else 0]['saved_map']
pts=list(m['points'])+[m['start'],m['boss']]
def k(c): return (c['row'],c['col'])
nodes=sorted({k(p['coord']) for p in pts}); edges=[]
for p in pts:
    for ch in p.get('children',[]) : edges.append((k(p['coord']),k(ch)))
tmap={'monster':'Monster','elite':'Elite','shop':'Shop','rest_site':'RestSite','treasure':'Treasure','unknown':'Unknown','ancient':'Ancient','boss':'Boss'}
print(' '.join(f'{r},{c}' for r,c in nodes))
print(' '.join(f'{a[0]},{a[1]}->{b[0]},{b[1]}' for a,b in sorted(edges)))
print(' '.join(f"{p['coord']['row']},{p['coord']['col']}={tmap.get(p['type'],p['type'])}" for p in sorted(pts,key=lambda p:k(p['coord']))))
