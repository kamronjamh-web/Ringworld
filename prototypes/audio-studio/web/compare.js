'use strict';

const players = [];
const status = document.getElementById('status');
const matchLevels = document.getElementById('match-levels');

function node(tag, className, text) {
  const result = document.createElement(tag);
  if (className) result.className = className;
  if (text !== undefined) result.textContent = text;
  return result;
}

function audioURL(value) {
  const url = new URL(value, window.location.href);
  if (url.origin !== window.location.origin || !url.pathname.startsWith('/api/jobs/')) {
    throw new Error('Comparison audio must use local Studio job files.');
  }
  return url.href;
}

function applyLevels() {
  for (const {audio, gain, label} of players) {
    audio.volume = matchLevels.checked ? gain : 1;
    const attenuation = gain > 0 ? (20 * Math.log10(gain)).toFixed(1) : '−∞';
    label.textContent = matchLevels.checked ? `Playback adjustment: ${attenuation} dB` : 'Playback adjustment: none';
  }
}

function takeCard(letter, take, direction) {
  const gain = Number(take.playback_gain);
  if (!Number.isFinite(gain) || gain < 0 || gain > 1) throw new Error('Invalid playback level in comparison data.');
  const wav = audioURL(take.wav_url);
  const ogg = audioURL(take.ogg_url);
  const card = node('article', 'comparison-take');
  card.append(node('h3', '', `${letter} · ${take.backend}`));
  card.append(node('span', 'permanent-id', take.take_id));
  const audio = document.createElement('audio');
  audio.controls = true;
  audio.preload = 'none';
  audio.src = wav;
  audio.setAttribute('aria-label', `${direction}, ${letter}, ${take.backend}, take ${take.take_id}`);
  audio.addEventListener('play', () => {
    for (const other of players) if (other.audio !== audio) other.audio.pause();
  });
  card.append(audio);
  const measured = Number(take.rms_dbfs);
  if (Number.isFinite(measured)) card.append(node('p', 'hint', `Original RMS: ${measured.toFixed(1)} dBFS`));
  const adjustment = node('p', 'hint');
  card.append(adjustment);
  const links = node('div', 'links');
  for (const [format, url] of [['WAV', wav], ['OGG', ogg]]) {
    const link = node('a', '', `↓ ${format}`);
    link.href = url;
    link.download = '';
    links.append(link);
  }
  card.append(links);
  players.push({audio, gain, label: adjustment});
  return card;
}

async function loadComparison() {
  try {
    const response = await fetch('/comparison-data.json', {cache: 'no-store'});
    if (!response.ok) throw new Error('Comparison data is not ready yet. Refresh this page after generation finishes.');
    const data = await response.json();
    if (!Array.isArray(data.pairs) || data.pairs.length === 0) throw new Error('No comparison pairs are available yet.');
    const fragment = document.createDocumentFragment();
    for (const pair of data.pairs) {
      const section = node('section', 'comparison-pair');
      section.append(node('h2', '', pair.direction));
      section.append(node('p', 'hint', `Seed ${pair.seed} · ${pair.duration_seconds}s · ${pair.steps} steps · guidance ${pair.guidance_scale}`));
      const grid = node('div', 'ab-grid');
      grid.append(takeCard('A', pair.A, pair.direction), takeCard('B', pair.B, pair.direction));
      section.append(grid);
      fragment.append(section);
    }
    document.getElementById('title').textContent = data.title || 'Frozen-lake sounds · A/B';
    document.title = `${data.title || 'Audio comparison'} · Ringworld`;
    document.getElementById('note').textContent = data.note || '';
    document.getElementById('pairs').replaceChildren(fragment);
    applyLevels();
    status.textContent = `${data.pairs.length} pairs ready. Starting one player pauses the others.`;
  } catch (error) {
    status.className = 'error';
    status.textContent = error.message || 'Could not load the comparison.';
  }
}

matchLevels.addEventListener('change', applyLevels);
loadComparison();
