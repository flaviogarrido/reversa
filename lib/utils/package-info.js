import { readFileSync } from 'fs';
import { fileURLToPath } from 'url';
import { dirname, join } from 'path';

// Nome e versão do pacote que está rodando. Em um fork publicado com escopo
// (ex.: @flaviogarrido/reversa), tudo que fala com o npm ou sugere comandos ao
// usuário precisa usar este nome, e não o literal "reversa" do upstream.
const pkgPath = join(dirname(fileURLToPath(import.meta.url)), '..', '..', 'package.json');
const pkg = JSON.parse(readFileSync(pkgPath, 'utf8').replace(/^﻿/, ''));

export const PACKAGE_NAME = pkg.name;
export const PACKAGE_VERSION = pkg.version;

/** Comando npx para este pacote, ex.: "npx @flaviogarrido/reversa install". */
export function npxCommand(subcommand = '') {
  return `npx ${PACKAGE_NAME}${subcommand ? ' ' + subcommand : ''}`;
}

/** URL do registro npm para a última versão, com o "/" do escopo codificado. */
export function registryLatestUrl(name = PACKAGE_NAME) {
  return `https://registry.npmjs.org/${name.replace('/', '%2F')}/latest`;
}
