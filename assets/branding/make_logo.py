"""Draws the Portalito logo: a lit arched doorway with a play button, violet->magenta->orange.

    python3 assets/branding/make_logo.py   # -> portalito-icon.png, portalito-icon-transparent.png, portalito-banner.png

Needs Pillow and the Noto Sans ExtraBold font (for the banner's wordmark).
"""
from PIL import Image, ImageDraw, ImageFont, ImageFilter
import math, sys
import os
OUT=os.path.dirname(os.path.abspath(__file__))  # writes next to this script
STOPS=[(0.0,(123,47,247)),(0.55,(241,7,163)),(1.0,(255,138,0))]  # violet -> magenta -> orange

def lerp(a,b,t): return tuple(int(a[i]+(b[i]-a[i])*t) for i in range(3))
def grad_color(t):
    for (t0,c0),(t1,c1) in zip(STOPS,STOPS[1:]):
        if t<=t1: return lerp(c0,c1,(t-t0)/(t1-t0))
    return STOPS[-1][1]

def vertical_gradient(w,h,top,bottom):
    g=Image.new("RGB",(w,h)); px=g.load()
    for y in range(h):
        t=min(max((y-top)/max(bottom-top,1),0),1); c=grad_color(t)
        for x in range(w): px[x,y]=c
    return g

def arch_mask(size, box):
    x0,y0,x1,y1=box; r=(x1-x0)/2
    m=Image.new("L",size,0); d=ImageDraw.Draw(m)
    d.pieslice([x0,y0,x1,y0+2*r],180,360,fill=255)
    d.rectangle([x0,y0+r,x1,y1],fill=255)
    return m

def mark(S, bg=None):
    """The icon on an SxS canvas: gradient arch, dark glowing opening, white play triangle."""
    img=Image.new("RGBA",(S,S),(0,0,0,0) if bg is None else bg)
    outer=(0.22*S,0.10*S,0.78*S,0.82*S)
    grad=vertical_gradient(S,S,outer[1],outer[3])
    img.paste(grad,(0,0),arch_mask((S,S),outer))
    t=0.075*S
    inner=(outer[0]+t,outer[1]+t,outer[2]-t,outer[3]-t*0.0)
    # opening: deep indigo with a soft light coming from inside
    opening=Image.new("RGB",(S,S),(26,16,51))
    glow=Image.new("L",(S,S),0); gd=ImageDraw.Draw(glow)
    cx,cy=(inner[0]+inner[2])/2, inner[1]+(inner[3]-inner[1])*0.58
    gd.ellipse([cx-0.17*S,cy-0.20*S,cx+0.17*S,cy+0.20*S],fill=150)
    glow=glow.filter(ImageFilter.GaussianBlur(0.07*S))
    opening=Image.composite(Image.new("RGB",(S,S),(120,70,200)),opening,glow)
    img.paste(opening,(0,0),arch_mask((S,S),inner))
    # threshold step: makes it read as a doorway, not a magnet
    d0=ImageDraw.Draw(img); st=0.045*S; ext=0.05*S
    d0.rounded_rectangle([outer[0]-ext,outer[3]-st*0.15,outer[2]+ext,outer[3]+st],radius=st/2,fill=STOPS[-1][1])
    # play triangle, optically centred (nudged right)
    d=ImageDraw.Draw(img); h=0.20*S; w=h*math.sqrt(3)/2
    px=cx+w*0.12
    d.polygon([(px-w/2,cy-h/2),(px-w/2,cy+h/2),(px+w/2,cy)],fill=(255,255,255))
    return img

def save(img, name, size):
    img.resize(size, Image.LANCZOS).save(f"{OUT}/{name}"); print("wrote",name,size)

R=4  # supersampling
# 1) square icon, transparent and on a dark rounded tile
save(mark(512*R),"portalito-icon-transparent.png",(512,512))
tile=Image.new("RGBA",(512*R,512*R),(0,0,0,0)); m=Image.new("L",tile.size,0)
ImageDraw.Draw(m).rounded_rectangle([0,0,512*R-1,512*R-1],radius=96*R,fill=255)
tile.paste((16,16,26,255),(0,0),m); tile.alpha_composite(mark(512*R))
save(tile,"portalito-icon.png",(512,512))
# 2) 16:9 banner: mark + wordmark on dark
W,H=1280*R,720*R
ban=Image.new("RGBA",(W,H),(16,16,26,255))
ms=int(H*0.70)
font=ImageFont.truetype("/usr/share/fonts/truetype/noto/NotoSans-ExtraBold.ttf",int(H*0.19))
d=ImageDraw.Draw(ban); text="Portalito"; bb=d.textbbox((0,0),text,font=font)
mark_visible=int(ms*0.66)          # the arch spans ~0.17..0.83 of its canvas (with the step)
gap=int(H*0.06); group=mark_visible+gap+(bb[2]-bb[0])
left=(W-group)//2-int(ms*0.17)
ban.alpha_composite(mark(ms),(left,int((H-ms)/2)))
bx=left+int(ms*0.17)+mark_visible+gap
ty=int((H-(bb[3]-bb[1]))/2)-bb[1]
d.text((bx,ty),text,font=font,fill=(255,255,255))
# a thin gradient underline under the wordmark
uw=bb[2]-bb[0]; uy=ty+bb[3]+int(H*0.035); ul=vertical_gradient(uw,int(H*0.018),0,1)
ul=Image.new("RGB",(uw,int(H*0.018)))
for x in range(uw):
    c=grad_color(x/uw)
    for y in range(ul.size[1]): ul.putpixel((x,y),c)
ban.paste(ul,(bx,uy))
save(ban,"portalito-banner.png",(1280,720))
