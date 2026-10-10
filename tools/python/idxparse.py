import struct,sys
def parse(path):
    d=open(path,'rb').read()[32:]
    I=lambda o: struct.unpack('<i',d[o:o+4])[0]
    st={'levels':{},'fan':{}}
    entries=[]; nodes=[]
    def node(o):
        off=I(o); bb=struct.unpack('<4f',d[o+4:o+20]); lvl=I(o+20); n=I(o+24)
        end=o+20+off; p=o+28
        st['levels'][lvl]=st['levels'].get(lvl,0)+1
        st['fan'].setdefault(lvl,[]).append(n)
        kids=[]
        if lvl>0:
            for k in range(n):
                b,p=node(p); kids.append(b)
        else:
            for k in range(n):
                entries.append(struct.unpack('<4f3i',d[p:p+28])); p+=28
        assert p==end,(hex(o),hex(p),hex(end))
        nodes.append((lvl,bb,kids))
        return bb,end
    hdr=struct.unpack('<4s3i',d[:16])
    p=16; tops=0
    while p<len(d):
        _,p=node(p); tops+=1
    return hdr,tops,st,entries,nodes
if __name__=='__main__':
    for f in sys.argv[1:]:
        hdr,tops,st,en,_=parse(f)
        print(f,hdr,'tops',tops,'levels',st['levels'])
        for l,v in st['fan'].items(): print('   lvl',l,'fan min/max/avg',min(v),max(v),round(sum(v)/len(v),2))
        print('  entries',len(en),en[:2],en[-1])
