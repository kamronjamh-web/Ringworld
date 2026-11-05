'use strict';
const $ = id => document.getElementById(id);
let health = null;
let submitting = false;
const signatures = new Map();
const prompts = {
  'stable-audio-open': [
    ['Metal door', 'A heavy sci-fi metal door sliding open, hydraulic hiss, deep mechanical clunk, close microphone, no music.'],
    ['Reactor', 'A futuristic power reactor humming steadily, low electrical drone, gentle pulsing mechanical vibration, no speech, no music.'],
    ['Habitat', 'Quiet wind through an immense abandoned orbital habitat, distant ventilation, sparse metal creaks, immersive ambient field recording, no music.']
  ],
  'ace-step': [
    ['Exploration', 'Instrumental atmospheric science-fiction exploration music, warm analog synthesizer pads, delicate arpeggios, spacious slow evolving harmony, mysterious and hopeful, no vocals.'],
    ['Tension', 'Instrumental tense science-fiction soundtrack, low pulsing synth bass, restrained industrial percussion, dark evolving textures, slow rising suspense, no vocals.'],
    ['Sanctuary', 'Instrumental peaceful ambient soundtrack, soft piano motifs over luminous synthesizer pads, spacious and calm, gentle sense of discovery, no vocals.']
  ]
};

function element(tag, className, text) {
  const node = document.createElement(tag);
  if (className) node.className = className;
  if (text !== undefined) node.textContent = text;
  return node;
}

function configureEngine(reset = true) {
  const stable = $('engine').value === 'stable-audio-open';
  $('duration').min = stable ? '0.5' : '10';
  $('duration').max = stable ? '47' : '120';
  $('duration').step = stable ? '0.5' : '1';
  if (reset) $('duration').value = stable ? '5' : '30';
  $('prompt').maxLength = stable ? 2000 : 512;
  $('engine-hint').textContent = stable ? 'Sound effects, textures, and ambience · 0.5–47 seconds' : 'Instrumental music · 10–120 seconds · 8-step turbo';
  document.querySelectorAll('.stable-only').forEach(node => { node.hidden = !stable; });
  document.querySelectorAll('.ace-only').forEach(node => { node.hidden = stable; });
  $('presets').replaceChildren();
  for (const [name, prompt] of prompts[$('engine').value]) {
    const button = element('button', '', name);
    button.type = 'button';
    button.addEventListener('click', () => { $('prompt').value = prompt; $('prompt').focus(); });
    $('presets').append(button);
  }
  updateAvailability();
}

function updateAvailability() {
  const stable = $('engine').value === 'stable-audio-open';
  const selected = stable ? health?.stable_backends?.[$('backend').value] || health?.engines?.['stable-audio-open'] : health?.engines?.['ace-step'];
  $('backend-hint').textContent = stable && selected ? selected.message : '';
  $('generate').disabled = submitting || !selected?.ready;
  if (selected && !selected.ready) $('feedback').textContent = selected.message;
  else if (!submitting && $('feedback').dataset.unavailable === 'true') $('feedback').textContent = '';
  $('feedback').dataset.unavailable = selected && !selected.ready ? 'true' : 'false';
}

async function request(url, options) {
  const response = await fetch(url, options);
  const body = await response.json();
  if (!response.ok) {
    const detail = typeof body.detail === 'string' ? body.detail : JSON.stringify(body.detail || body);
    throw new Error(detail);
  }
  return body;
}

function link(label, url, download = false) {
  const a = element('a', '', label);
  a.href = url;
  if (download) a.download = '';
  else { a.target = '_blank'; a.rel = 'noopener'; }
  return a;
}

function reuse(job) {
  const settings = job.settings;
  $('engine').value = settings.engine;
  configureEngine();
  for (const name of ['prompt', 'duration', 'variants', 'seed', 'negative_prompt', 'steps', 'guidance_scale', 'bpm']) {
    if (settings[name] !== undefined && settings[name] !== null) $(name).value = settings[name];
  }
  $('backend').value = settings.backend || 'diffusers';
  updateAvailability();
  $('prompt').focus();
  window.scrollTo({top: 0, behavior: 'smooth'});
}

function idLabel(id, kind) {
  const group = element('div', 'id-label');
  const text = element('span', 'permanent-id', id);
  const button = element('button', 'copy-id', 'Copy ID');
  button.type = 'button';
  button.setAttribute('aria-label', `Copy ${kind} ID ${id}`);
  button.addEventListener('click', async () => {
    try {
      if (!navigator.clipboard?.writeText) throw new Error('Clipboard unavailable');
      await navigator.clipboard.writeText(id);
      $('feedback').textContent = `Copied ${id}`;
    } catch (error) {
      const range = document.createRange();
      range.selectNodeContents(text);
      const selection = window.getSelection();
      selection.removeAllRanges();
      selection.addRange(range);
      $('feedback').textContent = `Could not copy automatically. ${id} is selected; press Ctrl+C or copy the visible ID.`;
    }
  });
  group.append(text, button);
  return group;
}

function jobCard(job) {
  const card = element('article', 'job');
  card.id = 'job-' + job.id;
  const head = element('div', 'job-head');
  head.append(idLabel(job.generation_id, 'generation'), element('span', 'badge ' + job.status, job.status.replaceAll('_', ' ')));
  card.append(head);
  if (job.settings.engine === 'stable-audio-open') {
    card.append(element('span', 'backend-badge', `Backend: ${job.settings.backend || job.backend || 'diffusers'}`));
  }
  card.append(element('h3', '', job.settings.prompt));
  card.append(element('p', 'job-info', `${job.settings.engine === 'ace-step' ? 'ACE-Step 1.5 · instrumental' : 'Stable Audio Open'} · ${job.settings.duration}s · ${new Date(job.created_at).toLocaleString()}`));
  if (job.status === 'failed') card.append(element('pre', '', job.error || job.message));
  else if (job.status !== 'succeeded') card.append(element('p', 'hint', job.message));
  for (const take of job.outputs) {
    const section = element('div', 'take');
    const label = element('div', 'take-label');
    label.append(idLabel(take.take_id, 'take'), element('span', '', `${take.duration_seconds.toFixed(2)}s · ${take.sample_rate / 1000} kHz`));
    const base = `/api/jobs/${encodeURIComponent(job.id)}/files/`;
    const audio = document.createElement('audio');
    audio.controls = true;
    audio.preload = 'none';
    audio.src = base + encodeURIComponent(take.wav);
    audio.setAttribute('aria-label', `Take ${take.take_id}: ${job.settings.prompt}`);
    const links = element('div', 'links');
    links.append(link('↓ WAV', base + encodeURIComponent(take.wav), true), link('↓ OGG', base + encodeURIComponent(take.ogg), true));
    section.append(label, element('p', 'job-info', `Take ${take.variation} · seed ${take.seed}`), audio, links);
    card.append(section);
  }
  const links = element('div', 'links');
  const base = `/api/jobs/${encodeURIComponent(job.id)}/files/`;
  links.append(link('Metadata', base + 'metadata.json'));
  if (job.status === 'failed' || job.status === 'generating') links.append(link('Worker log', base + 'worker-stderr.log'));
  const button = element('button', 'reuse', 'Reuse settings');
  button.type = 'button';
  button.addEventListener('click', () => reuse(job));
  links.append(button);
  card.append(links);
  return card;
}

async function refresh() {
  try {
    const [nextHealth, result] = await Promise.all([request('/api/health'), request('/api/jobs')]);
    health = nextHealth;
    for (const [id, engine, title] of [['stable-status', 'stable-audio-open', 'SOUND EFFECTS'], ['ace-status', 'ace-step', 'MUSIC']]) {
      const state = health.engines[engine];
      $(id).className = state.ready ? 'ready' : '';
      $(id).textContent = `${state.ready ? '●' : '○'} ${title} · ${state.message}`;
    }
    updateAvailability();
    const active = result.jobs.filter(job => !['succeeded', 'failed'].includes(job.status)).length;
    $('queue-status').textContent = active ? `${active} active · GPU jobs run one at a time` : `${result.jobs.length} jobs · ready`;
    $('empty').hidden = result.jobs.length > 0;
    // Keep completed audio elements intact during polling, including their playback positions.
    for (let i = result.jobs.length - 1; i >= 0; i--) {
      const job = result.jobs[i];
      const signature = JSON.stringify([job.generation_id, job.backend, job.settings.backend, job.status, job.message, job.outputs]);
      if (signatures.get(job.id) === signature) continue;
      const card = jobCard(job);
      const old = $('job-' + job.id);
      if (old) old.replaceWith(card);
      else $('jobs').prepend(card);
      signatures.set(job.id, signature);
    }
  } catch (error) {
    $('queue-status').textContent = 'Server unavailable · retrying';
    $('feedback').textContent = error.message;
    $('generate').disabled = true;
  } finally {
    setTimeout(refresh, 2500);
  }
}

$('engine').addEventListener('change', () => configureEngine());
$('backend').addEventListener('change', updateAvailability);
$('generator').addEventListener('submit', async event => {
  event.preventDefault();
  submitting = true;
  updateAvailability();
  const stable = $('engine').value === 'stable-audio-open';
  const body = {engine: $('engine').value, prompt: $('prompt').value, duration: Number($('duration').value),
    backend: stable ? $('backend').value : 'diffusers',
    variants: Number($('variants').value), seed: $('seed').value === '' ? null : Number($('seed').value),
    steps: stable ? Number($('steps').value) : 8,
    guidance_scale: stable ? Number($('guidance_scale').value) : 7,
    negative_prompt: stable ? $('negative_prompt').value : '',
    bpm: !stable && $('bpm').value !== '' ? Number($('bpm').value) : null};
  try {
    const job = await request('/api/jobs', {method: 'POST', headers: {'Content-Type': 'application/json'}, body: JSON.stringify(body)});
    $('feedback').textContent = `Queued ${job.generation_id} · seed ${job.settings.seed}. You can queue another prompt while this runs.`;
    $('feedback').classList.remove('error');
  } catch (error) {
    $('feedback').textContent = error.message;
    $('feedback').classList.add('error');
  } finally {
    submitting = false;
    updateAvailability();
  }
});
configureEngine();
refresh();
