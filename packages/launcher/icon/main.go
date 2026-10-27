// Command icon generates the agbrowser icon set.
//
// The mark is an original design: a rounded-square "portal" frame holding an
// orbital agent node. It is drawn analytically with 4x supersampling so it
// stays crisp from 16px favicons up to 512px app icons.
//
// Output:
//   assets/icon.ico          multi-resolution Windows icon (16..256)
//   assets/icon-<n>.png      individual PNGs
//   ../extensions/*/icons/   extension icon triples
package main

import (
	"bytes"
	"encoding/binary"
	"fmt"
	"image"
	"image/color"
	"image/png"
	"math"
	"os"
	"path/filepath"
)

// ss is the supersampling factor per axis.
const ss = 4

var (
	brandA = color.NRGBA{0x3B, 0x82, 0xF6, 0xFF} // electric blue
	brandB = color.NRGBA{0x22, 0xD3, 0xEE, 0xFF} // cyan
	accent = color.NRGBA{0xA7, 0xF3, 0xD0, 0xFF} // mint node
	deepBG = color.NRGBA{0x0B, 0x0F, 0x18, 0xFF}
)

// rgb blends two colours.
func rgb(a, b color.NRGBA, t float64) color.NRGBA {
	if t < 0 {
		t = 0
	}
	if t > 1 {
		t = 1
	}
	return color.NRGBA{
		uint8(float64(a.R) + (float64(b.R)-float64(a.R))*t),
		uint8(float64(a.G) + (float64(b.G)-float64(a.G))*t),
		uint8(float64(a.B) + (float64(b.B)-float64(a.B))*t),
		uint8(float64(a.A) + (float64(a.A)-float64(b.A))*t),
	}
}

// mix composites src over dst with alpha.
func mix(dst, src color.NRGBA) color.NRGBA {
	if src.A == 255 {
		return src
	}
	if src.A == 0 {
		return dst
	}
	a := float64(src.A) / 255
	return color.NRGBA{
		uint8(float64(src.R)*a + float64(dst.R)*(1-a)),
		uint8(float64(src.G)*a + float64(dst.G)*(1-a)),
		uint8(float64(src.B)*a + float64(dst.B)*(1-a)),
		uint8(float64(dst.A)*(1-a) + 255*a),
	}
}

// mixA composites src over dst using an explicit coverage factor.
func mixA(dst, src color.NRGBA, coverage float64) color.NRGBA {
	c := clamp01(coverage)
	if c == 0 {
		return dst
	}
	if c >= 1 {
		return src
	}
	a := float64(src.A) / 255
	// Effective alpha of the source after coverage scaling.
	sa := a * c
	return color.NRGBA{
		uint8(float64(src.R)*sa + float64(dst.R)*(1-sa)),
		uint8(float64(src.G)*sa + float64(dst.G)*(1-sa)),
		uint8(float64(src.B)*sa + float64(dst.B)*(1-sa)),
		uint8(float64(dst.A)*(1-sa) + 255*sa),
	}
}

// roundedRectSDF returns the signed distance to a rounded rectangle.
// Negative means inside.
func roundedRectSDF(x, y, cx, cy, halfW, halfH, r float64) float64 {
	dx := math.Abs(x-cx) - (halfW - r)
	dy := math.Abs(y-cy) - (halfH - r)
	dx = math.Max(dx, 0)
	dy = math.Max(dy, 0)
	return math.Hypot(dx, dy) + math.Min(math.Max(dx, dy), 0) - r
}

// drawPixel computes the colour of a single sample point.
func drawPixel(x, y, size float64, style int) color.NRGBA {
	s := float64(size)
	cx, cy := s/2, s/2

	// Outer rounded square.
	outerR := s * 0.235
	outerD := roundedRectSDF(x, y, cx, cy, s*0.46, s*0.46, outerR)

	var col color.NRGBA

	// Background plate with a diagonal brand gradient.
	bg := rgb(brandA, brandB, (x+y)/(2*s))
	// Deepen the lower-left for depth.
	bg = mixA(bg, deepBG, math.Max(0, 0.35-0.35*((x+y)/(2*s))))
	col = mixA(col, bg, clamp01(-outerD*ss))

	switch style {
	case 0: // full app icon
		col = drawMark(col, x, y, s, 1.0)
	case 1: // monochrome (taskbar / installer)
		col = drawMark(col, x, y, s, 0.0)
	}
	return col
}

// drawMark paints the orbital agent glyph on top of the plate.
//
// The glyph is an open arc (the "portal") with a travelling node on the orbit
// and a solid core at the centre. Membership is decided in polar coordinates
// around the centre, which avoids the artefacts that arise from approximating
// a curve with many short segments.
func drawMark(base color.NRGBA, x, y, s, colourScale float64) color.NRGBA {
	cx, cy := s/2, s/2
	col := base

	ringR := s * 0.275
	ringW := s * 0.072
	start := -2.35
	end := 1.85

	dx := x - cx
	dy := y - cy
	rad := math.Hypot(dx, dy)
	ang := math.Atan2(dy, dx)

	// Distance from the ring band, in the radial direction.
	radial := math.Abs(rad - ringR)
	// Half the stroke width, with a small outward allowance so the stroke
	// reads as a clean band.
	half := ringW / 2

	// Is the angle inside the open arc? The arc runs counter-clockwise from
	// `start` to `end`, leaving a gap in the lower right.
	inArc := angInArc(ang, start, end)

	if inArc && radial <= half {
		// Anti-alias across the band edges using the sample spacing.
		edge := half - radial
		g := rgb(brandB, accent, (ang-start)/(end-start+0.0001)*0.5+0.5)
		if colourScale == 0 {
			g = color.NRGBA{0xFF, 0xFF, 0xFF, 0xFF}
		}
		col = mixA(col, g, clamp01(edge*ss*1.2))
	}

	// The agent node travelling on the orbit.
	nodeAng := 0.15
	nx := cx + math.Cos(nodeAng)*ringR
	ny := cy + math.Sin(nodeAng)*ringR
	nodeR := s * 0.082
	if nd := math.Hypot(x-nx, y-ny); nd <= nodeR {
		g := accent
		if colourScale == 0 {
			g = color.NRGBA{0xFF, 0xFF, 0xFF, 0xFF}
		}
		col = mixA(col, g, clamp01((nodeR-nd)*ss))
	}

	// The core: a small solid disc the orbit revolves around.
	coreR := s * 0.085
	if cd := math.Hypot(x-cx, y-cy); cd <= coreR {
		g := color.NRGBA{0xFF, 0xFF, 0xFF, 0xFF}
		col = mixA(col, g, clamp01((coreR-cd)*ss))
	}

	return col
}

// angInArc reports whether an angle lies within the open arc, handling the
// wrap-around at +/-pi.
func angInArc(ang, start, end float64) bool {
	norm := func(a float64) float64 {
		for a < 0 {
			a += 2 * math.Pi
		}
		for a >= 2*math.Pi {
			a -= 2 * math.Pi
		}
		return a
	}
	a := norm(ang)
	s0 := norm(start)
	e0 := norm(end)
	if s0 <= e0 {
		return a >= s0 && a <= e0
	}
	return a >= s0 || a <= e0
}

func clamp01(v float64) float64 {
	if v < 0 {
		return 0
	}
	if v > 1 {
		return 1
	}
	return v
}

// render produces an anti-aliased image of the given size.
func render(size int, style int) *image.NRGBA {
	img := image.NewNRGBA(image.Rect(0, 0, size, size))
	for y := 0; y < size; y++ {
		for x := 0; x < size; x++ {
			var r, g, b, a float64
			for sy := 0; sy < ss; sy++ {
				for sx := 0; sx < ss; sx++ {
					px := float64(x) + (float64(sx)+0.5)/ss
					py := float64(y) + (float64(sy)+0.5)/ss
					c := drawPixel(px, py, float64(size), style)
					r += float64(c.R)
					g += float64(c.G)
					b += float64(c.B)
					a += float64(c.A)
				}
			}
			n := float64(ss * ss)
			img.SetNRGBA(x, y, color.NRGBA{
				uint8(r / n), uint8(g / n), uint8(b / n), uint8(a / n),
			})
		}
	}
	return img
}

func encodePNG(img image.Image) ([]byte, error) {
	var buf bytes.Buffer
	enc := png.Encoder{CompressionLevel: png.BestCompression}
	if err := enc.Encode(&buf, img); err != nil {
		return nil, err
	}
	return buf.Bytes(), nil
}

// encodeICO builds a Windows icon containing several PNG-compressed frames.
func encodeICO(frames [][]byte, sizes []int) ([]byte, error) {
	if len(frames) != len(sizes) {
		return nil, fmt.Errorf("frame/size mismatch")
	}
	var buf bytes.Buffer
	// ICONDIR
	binary.Write(&buf, binary.LittleEndian, uint16(0)) // reserved
	binary.Write(&buf, binary.LittleEndian, uint16(1)) // type: icon
	binary.Write(&buf, binary.LittleEndian, uint16(len(frames)))

	offset := 6 + 16*len(frames)
	for i, f := range frames {
		s := sizes[i]
		dim := byte(0)
		if s >= 256 {
			dim = 0
		} else {
			dim = byte(s)
		}
		buf.WriteByte(dim)             // width
		buf.WriteByte(dim)             // height
		buf.WriteByte(0)               // palette
		buf.WriteByte(0)               // reserved
		binary.Write(&buf, binary.LittleEndian, uint16(1)) // colour planes
		binary.Write(&buf, binary.LittleEndian, uint16(32)) // bits per pixel
		binary.Write(&buf, binary.LittleEndian, uint32(len(f)))
		binary.Write(&buf, binary.LittleEndian, uint32(offset))
		offset += len(f)
	}
	for _, f := range frames {
		buf.Write(f)
	}
	return buf.Bytes(), nil
}

func writeFile(path string, data []byte) error {
	if err := os.MkdirAll(filepath.Dir(path), 0o755); err != nil {
		return err
	}
	return os.WriteFile(path, data, 0o644)
}

func main() {
	root := "."
	if len(os.Args) > 1 {
		root = os.Args[1]
	}
	assets := filepath.Join(root, "assets")
	extRoot := filepath.Join(root, "..", "extensions")

	// App icon sizes.
	appSizes := []int{16, 24, 32, 48, 64, 128, 256}

	// Full-colour app icon frames for the .ico.
	var icoFrames [][]byte
	var icoSizes []int
	for _, s := range appSizes {
		img := render(s, 0)
		data, err := encodePNG(img)
		if err != nil {
			panic(err)
		}
		if err := writeFile(filepath.Join(assets, fmt.Sprintf("icon-%d.png", s)), data); err != nil {
			panic(err)
		}
		if s == 16 || s == 24 || s == 32 || s == 48 || s == 64 || s == 128 || s == 256 {
			icoFrames = append(icoFrames, data)
			icoSizes = append(icoSizes, s)
		}
	}

	ico, err := encodeICO(icoFrames, icoSizes)
	if err != nil {
		panic(err)
	}
	if err := writeFile(filepath.Join(assets, "icon.ico"), ico); err != nil {
		panic(err)
	}

	// Monochrome variant for the MSI shortcut / installer.
	for _, s := range []int{16, 32, 48, 256} {
		img := render(s, 1)
		data, _ := encodePNG(img)
		if err := writeFile(filepath.Join(assets, fmt.Sprintf("icon-mono-%d.png", s)), data); err != nil {
			panic(err)
		}
	}

	// Extension icon triples. Each pack gets the shared mark with its own
	// accent tint so the three are distinguishable in the toolbar.
	tints := map[string][2]color.NRGBA{
		"agb-shield": {color.NRGBA{0x34, 0xD3, 0x99, 0xFF}, color.NRGBA{0x0E, 0x9F, 0x6E, 0xFF}},
		"agb-speed":  {color.NRGBA{0xFB, 0xBF, 0x24, 0xFF}, color.NRGBA{0xF5, 0x9E, 0x0B, 0xFF}},
		"agb-search": {color.NRGBA{0xC0, 0x84, 0xFC, 0xFF}, color.NRGBA{0x93, 0x3C, 0xF0, 0xFF}},
	}
	for pack, tint := range tints {
		dir := filepath.Join(extRoot, pack, "icons")
		if _, err := os.Stat(dir); err != nil {
			continue
		}
		for _, s := range []int{16, 48, 128} {
			img := renderTinted(s, tint[0], tint[1])
			data, _ := encodePNG(img)
			if err := writeFile(filepath.Join(dir, fmt.Sprintf("icon%d.png", s)), data); err != nil {
				panic(err)
			}
		}
		fmt.Printf("extension icons written: %s\n", pack)
	}

	fmt.Println("icons generated in", assets)
}

// renderTinted renders the mark using a pack-specific accent palette.
func renderTinted(size int, c1, c2 color.NRGBA) *image.NRGBA {
	img := image.NewNRGBA(image.Rect(0, 0, size, size))
	s := float64(size)
	for y := 0; y < size; y++ {
		for x := 0; x < size; x++ {
			var r, g, b, a float64
			for sy := 0; sy < ss; sy++ {
				for sx := 0; sx < ss; sx++ {
					px := float64(x) + (float64(sx)+0.5)/ss
					py := float64(y) + (float64(sy)+0.5)/ss
					c := tintedPixel(px, py, s, c1, c2)
					r += float64(c.R)
					g += float64(c.G)
					b += float64(c.B)
					a += float64(c.A)
				}
			}
			n := float64(ss * ss)
			img.SetNRGBA(x, y, color.NRGBA{
				uint8(r / n), uint8(g / n), uint8(b / n), uint8(a / n),
			})
		}
	}
	return img
}

func tintedPixel(x, y, s float64, c1, c2 color.NRGBA) color.NRGBA {
	cx, cy := s/2, s/2
	outerR := s * 0.235
	outerD := roundedRectSDF(x, y, cx, cy, s*0.46, s*0.46, outerR)
	var col color.NRGBA
	bg := rgb(c1, c2, (x+y)/(2*s))
	col = mixA(col, bg, clamp01(-outerD*ss))

	ringR := s * 0.275
	ringW := s * 0.072
	start, end := -2.35, 1.85

	dx := x - cx
	dy := y - cy
	rad := math.Hypot(dx, dy)
	ang := math.Atan2(dy, dx)
	radial := math.Abs(rad - ringR)
	half := ringW / 2

	white := color.NRGBA{0xFF, 0xFF, 0xFF, 0xFF}
	if angInArc(ang, start, end) && radial <= half {
		col = mixA(col, white, clamp01((half-radial)*ss*1.2))
	}
	nodeAng := 0.15
	nx := cx + math.Cos(nodeAng)*ringR
	ny := cy + math.Sin(nodeAng)*ringR
	nodeR := s * 0.082
	if nd := math.Hypot(x-nx, y-ny); nd <= nodeR {
		col = mixA(col, white, clamp01((nodeR-nd)*ss))
	}
	coreR := s * 0.085
	if cd := math.Hypot(x-cx, y-cy); cd <= coreR {
		col = mixA(col, white, clamp01((coreR-cd)*ss))
	}
	return col
}
