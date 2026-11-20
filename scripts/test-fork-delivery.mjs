#!/usr/bin/env node
// Testes da entrega do fork (pacote com escopo) e das correções do `reversa update`:
//
//  1. isInsideDir: editar .claude/skills/reversa-docs/ não pode bloquear a atualização
//     de .claude/skills/reversa/ (antes: startsWith sem a "/" final).
//  2. Migração upstream → fork: um projeto instalado pelo pacote `reversa` é atualizado
//     pelo pacote do fork mesmo que a versão instalada pareça maior ou igual.
//  3. A versão gravada é a do pacote em execução (não a do npm), e o pacote de origem
//     fica registrado em state.json.
//  4. Rodar o update de novo, já na versão atual, não faz nada.
import { spawnSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { existsSync, mkdtempSync, mkdirSync, readdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { isInsideDir } from '../lib/installer/manifest.js';
import { PACKAGE_NAME, PACKAGE_VERSION, registryLatestUrl } from '../lib/utils/package-info.js';

const ROOT = fileURLToPath(new URL('..', import.meta.url));
const CLI = join(ROOT, 'bin', 'reversa.js');
let failures = 0;
const check = (cond, msg) => { if (cond) console.log(`  ✓ ${msg}`); else { console.error(`  ✗ ${msg}`); failures++; } };
const sha = (text) => createHash('sha256').update(text).digest('hex');
const write = (file, text) => { mkdirSync(dirname(file), { recursive: true }); writeFileSync(file, text); };

console.log('=== isInsideDir');
check(isInsideDir('.claude/skills/reversa/SKILL.md', '.claude/skills/reversa'), 'arquivo dentro da pasta');
check(!isInsideDir('.claude/skills/reversa-docs/SKILL.md', '.claude/skills/reversa'), 'pasta com prefixo comum não casa');
check(isInsideDir('.claude\\skills\\reversa\\x.md', '.claude/skills/reversa'), 'separador do Windows normalizado');
check(isInsideDir('.claude/skills/reversa', '.claude/skills/reversa/'), 'a própria pasta, com barra final');

console.log('\n=== pacote');
check(PACKAGE_NAME !== 'reversa', `nome com escopo do fork (${PACKAGE_NAME})`);
check(!registryLatestUrl('@a/b').includes('@a/b/'), 'escopo codificado na URL do registro');

function fakeUpstreamProject(installedVersion) {
  const project = mkdtempSync(join(tmpdir(), 'reversa-fork-'));
  const oldOrchestrator = '---\nname: reversa\ndescription: versão antiga\n---\nantigo\n';
  const docsOriginal = '---\nname: reversa-docs\ndescription: original\n---\noriginal\n';
  const docsEdited = docsOriginal + '\nEDITADO PELO USUÁRIO\n';
  write(join(project, '.claude/skills/reversa/SKILL.md'), oldOrchestrator);
  write(join(project, '.claude/skills/reversa-docs/SKILL.md'), docsEdited);
  write(join(project, '.reversa/state.json'), JSON.stringify({
    version: installedVersion, engines: ['claude-code'], agents: ['reversa', 'reversa-docs'],
    output_folder: '_reversa_sdd', created_files: [],
  }, null, 2));
  write(join(project, '.reversa/version'), installedVersion);
  write(join(project, '.reversa/_config/files-manifest.json'), JSON.stringify({
    '.claude/skills/reversa/SKILL.md': sha(oldOrchestrator),
    '.claude/skills/reversa-docs/SKILL.md': sha(docsOriginal),
  }, null, 2));
  return { project, docsEdited };
}

const runUpdate = (project) => spawnSync(process.execPath, [CLI, 'update'], {
  cwd: project, input: 'y\n', encoding: 'utf8', timeout: 120_000, env: { ...process.env, FORCE_COLOR: '0' },
});

for (const installed of ['1.3.6', '99.0.0']) {
  console.log(`\n=== update de projeto instalado pelo upstream (v${installed})`);
  const { project, docsEdited } = fakeUpstreamProject(installed);
  try {
    const run = runUpdate(project);
    check(run.status === 0, `update terminou sem erro${run.status === 0 ? '' : `: ${run.stderr || run.stdout}`}`);

    const state = JSON.parse(readFileSync(join(project, '.reversa/state.json'), 'utf8'));
    check(state.package === PACKAGE_NAME, 'state.json registra o pacote de origem');
    check(state.version === PACKAGE_VERSION, `state.json na versão do pacote em execução (${state.version})`);
    check(readFileSync(join(project, '.reversa/version'), 'utf8') === PACKAGE_VERSION, '.reversa/version atualizado');

    const orchestrator = readFileSync(join(project, '.claude/skills/reversa/SKILL.md'), 'utf8');
    check(orchestrator === readFileSync(join(ROOT, 'agents/reversa/SKILL.md'), 'utf8'),
      'orquestrador atualizado mesmo com reversa-docs editada (bug de prefixo corrigido)');
    check(readFileSync(join(project, '.claude/skills/reversa-docs/SKILL.md'), 'utf8') === docsEdited,
      'skill editada pelo usuário preservada');

    const forkSkills = readdirSync(join(ROOT, 'agents')).filter(n => !n.startsWith('reversa') && n !== 'impeccable');
    for (const skill of forkSkills)
      check(existsSync(join(project, '.claude/skills', skill, 'SKILL.md')), `skill do fork instalada: ${skill}`);

    const again = runUpdate(project);
    check(again.status === 0 && /already on v/.test(again.stdout), 'segunda execução reconhece que já está atualizado');
  } finally {
    rmSync(project, { recursive: true, force: true });
  }
}

console.log(failures ? `\nRESULTADO: ✗ ${failures} falha(s)` : '\nRESULTADO: ✓ entrega do fork íntegra');
process.exit(failures ? 1 : 0);
