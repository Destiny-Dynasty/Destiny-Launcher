"""Inspect every lossless frame: erase blocks, missing track and fill regressions."""
import argparse
import json
from pathlib import Path
import shutil
import subprocess

import numpy as np
from PIL import Image, ImageDraw

parser = argparse.ArgumentParser()
parser.add_argument('recording', type=Path)
parser.add_argument('--scene', type=Path, help='Exact native backdrop from the same artwork and window size')
parser.add_argument('--expect-status', action='store_true', help='Check that progress text stays visible')
args = parser.parse_args()
recording = args.recording.resolve()
folder = recording.parent / 'frames'
folder.mkdir(exist_ok=True)
ffmpeg = shutil.which('ffmpeg')
ffprobe = shutil.which('ffprobe')
probe = json.loads(subprocess.check_output([ffprobe, '-v', 'error', '-select_streams', 'v:0',
    '-show_entries', 'stream=width,height,r_frame_rate', '-of', 'json', str(recording)]))['streams'][0]
width, height = probe['width'], probe['height']
a, b = map(int, probe['r_frame_rate'].split('/'))
fps = a / b
decoder = subprocess.Popen([ffmpeg, '-v', 'error', '-i', str(recording), '-f', 'rawvideo',
    '-pix_fmt', 'rgb24', '-'], stdout=subprocess.PIPE)
frame_bytes = width * height * 3
peaks = {'title_controls': (-1, None, -1), 'main_action': (-1, None, -1)}
bar_peak = (-1, None, -1)
dark_peak = (-1, None, -1)
scene_path = args.scene or recording.parent / 'scene.bmp'
scene = np.asarray(Image.open(scene_path).convert('RGB'), dtype=np.int16) if scene_path.exists() else None
progress_frames = 0
timeline = []
samples = []
index = 0
while True:
    data = bytearray()
    while len(data) < frame_bytes:
        chunk = decoder.stdout.read(frame_bytes - len(data))
        if not chunk:
            break
        data.extend(chunk)
    if not data:
        break
    if len(data) != frame_bytes:
        raise RuntimeError('Truncated decoded frame')
    pixels = np.frombuffer(data, dtype=np.uint8).reshape(height, width, 3)
    # Scene highlights are amber; the glyphs are warm gray. Large, neutral
    # near-white blocks in these controls are therefore useful anomaly signals.
    zones = {'title_controls': pixels[:int(height*.15), int(width*.86):],
             'main_action': pixels[int(height*.48):int(height*.87), int(width*.28):int(width*.72)]}
    counts = {}
    for name, crop in zones.items():
        white = (crop.min(axis=2) >= 240) & ((crop.max(axis=2).astype(np.int16) - crop.min(axis=2)) <= 12)
        count = int(white.sum())
        counts[name] = count
        if count > peaks[name][0]:
            peaks[name] = (count, bytes(data), index)
    bar = pixels[int(height*.867):int(height*.879), int(width*.043):int(width*.957)]
    gold = (np.abs(bar.astype(np.int16)-np.array([198,163,98])).max(axis=2) <= 2)
    gold_count = int(gold.sum())
    counts['gold_bar_pixels'] = gold_count
    rail = (np.abs(bar.astype(np.int16)-np.array([53,51,44])).max(axis=2) <= 2) | gold
    counts['track_pixels'] = int(rail.sum())
    status = pixels[int(height*.79):int(height*.84), int(width*.3):int(width*.7)]
    counts['status_ink_pixels'] = int((np.abs(status.astype(np.int16)-np.array([242,235,221])).max(axis=2)<=12).sum())
    # During installation this area must contain only the original backdrop.
    # Compare to the exact native GDI scene, rather than treating dark as safe.
    if scene is not None:
        if scene.shape != pixels.shape:
            raise RuntimeError('Native scene dimensions do not match recording')
        changed = 0
        if int(rail.sum()) > width * 2:
            progress_frames += 1
            y1,y2,x1,x2 = int(height*.76),int(height*.857),int(width*.083),int(width*.917)
            crop = pixels[y1:y2,x1:x2].astype(np.int16)
            reference = scene[y1:y2,x1:x2]
            flat_dark = np.abs(crop-np.array([16,16,15])).max(axis=2) <= 2
            changed = int((flat_dark & (np.abs(crop-reference).max(axis=2)>8)).sum())
        counts['unexpected_dark_pixels'] = changed
        if changed > dark_peak[0]:
            dark_peak = (changed, bytes(data), index)
    if gold_count > bar_peak[0]:
        bar_peak = (gold_count, bytes(data), index)
    timeline.append({'frame': index, 'seconds': round(index/fps, 4), **counts})
    if index % max(1, round(fps/2)) == 0:
        name = f'frame-{index:05d}.png'
        Image.fromarray(pixels).save(folder / name)
        samples.append((name, index/fps))
    index += 1
decoder.stdout.close()
if decoder.wait() != 0:
    raise RuntimeError('Video decoding failed')
peak_report = {}
for name, (count, data, frame) in peaks.items():
    if data is None:
        raise RuntimeError('No recorded frames')
    target = folder / f'peak-{name}.png'
    Image.frombytes('RGB', (width, height), data).save(target)
    peak_report[name] = {'max_near_white_pixels': count, 'frame': frame, 'seconds': round(frame/fps, 4), 'image': str(target)}
gold_count, data, frame = bar_peak
target = folder/'peak-progress.png'
Image.frombytes('RGB', (width,height), data).save(target)
peak_report['progress_bar'] = {'max_gold_pixels':gold_count, 'frame':frame,'seconds':round(frame/fps,4),'image':str(target)}
# Bound the phase by its first and last filled frames, so the intentional
# removal on completion is not mistaken for a missing track. Include *all*
# intervening frames, especially ones where the entire track has disappeared.
filled_frames = [i for i,row in enumerate(timeline) if row['gold_bar_pixels'] > 20]
bar_integrity = {'active_frames':0, 'missing_track_frames':[], 'fill_regression_frames':[]}
if args.expect_status:
    bar_integrity['missing_status_frames'] = []
if filled_frames:
    start,end = filled_frames[0],filled_frames[-1]
    bar_integrity['active_frames'] = end-start+1
    high_water = 0
    tolerance = max(14, round(height*.02))
    for row in timeline[start:end+1]:
        if row['track_pixels'] < width*2:
            bar_integrity['missing_track_frames'].append(row['frame'])
        if row['gold_bar_pixels'] < high_water-tolerance:
            bar_integrity['fill_regression_frames'].append(row['frame'])
        if args.expect_status and row['status_ink_pixels'] < 10:
            bar_integrity['missing_status_frames'].append(row['frame'])
        high_water = max(high_water,row['gold_bar_pixels'])
    anomalies = bar_integrity['missing_track_frames'] or bar_integrity['fill_regression_frames']
    if anomalies:
        subprocess.run([ffmpeg,'-v','error','-y','-i',str(recording),'-vf',
            f'select=eq(n\\,{anomalies[0]})','-frames:v','1',str(folder/'first-bar-anomaly.png')],check=True)
if scene is not None:
    count,data,frame = dark_peak
    target = folder/'peak-dark_rectangle.png'
    Image.frombytes('RGB',(width,height),data).save(target)
    peak_report['dark_rectangle'] = {'max_unexpected_dark_pixels':count,'progress_frames_checked':progress_frames,'frame':frame,'image':str(target)}
selected = samples[::max(1, len(samples)//12)][:12]
thumb_w, thumb_h = 320, round(320*height/width)
sheet = Image.new('RGB', (thumb_w*3, (thumb_h+26)*4), '#151513')
draw = ImageDraw.Draw(sheet)
for i, (name, seconds) in enumerate(selected):
    with Image.open(folder/name) as source:
        thumb = source.resize((thumb_w,thumb_h), Image.Resampling.LANCZOS)
    x, y = i%3*thumb_w, i//3*(thumb_h+26)
    sheet.paste(thumb,(x,y))
    draw.text((x+8,y+thumb_h+5),f'{seconds:.2f} s',fill='#d8c59f')
sheet.save(recording.parent/'contact-sheet.jpg',quality=92)
report = {'frames_analyzed': index, 'width': width, 'height': height, 'fps': fps,
          'seconds': round(index/fps,3), 'exported_samples': len(samples), 'peaks': peak_report,
          'bar_integrity':bar_integrity,
          'note': 'A pixel detector identifies candidates; visual review is still required. Every frame is retained in the lossless recording.'}
(recording.parent/'analysis.json').write_text(json.dumps(report,indent=2),encoding='utf-8')
(recording.parent/'frame-timeline.json').write_text(json.dumps(timeline),encoding='utf-8')
print(json.dumps(report,indent=2))
