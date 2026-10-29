#!/usr/bin/env node
// Turns README.md into the Docker Hub description (printed to stdout):
// - relative links point to GitHub (files and folders to github.com, images to raw.githubusercontent.com);
// - "#section" links point to that section of the README on GitHub (Docker Hub has no heading anchors);
// - GitHub-only alerts ("> [!NOTE]") become plain bold labels;
// - a link to the GitHub repository goes at <!-- dockerhub:github-link --> (or at the top);
// - code blocks are left alone; the result is cut to Docker Hub's 25,000-byte limit with a link to the rest.
//
// Usage: node dockerhub-readme.mjs README.md [owner/repo] [branch]
// owner/repo and branch default to GITHUB_REPOSITORY and GITHUB_REF_NAME (then the origin remote and main).
import { execSync } from 'node:child_process'
import { readFileSync } from 'node:fs'
import { posix } from 'node:path'

const [file = 'README.md', repoArg, refArg] = process.argv.slice(2)
const repo = repoArg || process.env.GITHUB_REPOSITORY || originRepo()
const ref = refArg || (process.env.GITHUB_REF_TYPE === 'branch' && process.env.GITHUB_REF_NAME) || 'main'
const home = `https://github.com/${repo}`
const dir = posix.dirname(file.replaceAll('\\', '/'))
const LIMIT = 25000

function resolve(target, image) {
  if (/^[a-z][a-z0-9+.-]*:/i.test(target) || target.startsWith('//')) return target
  if (target.startsWith('#')) return `${home}${target}`
  const [path, hash = ''] = target.split(/(?=#)/)
  const clean = posix.normalize(posix.join(dir === '.' ? '' : dir, path)).replace(/^(\.\/)+/, '')
  return image ? `https://raw.githubusercontent.com/${repo}/${ref}/${clean}` : `${home}/blob/${ref}/${clean}${hash}`
}

function convertLine(line) {
  return (
    line
      // ![alt](target "title") and [text](target "title")
      .replace(/(!?)\[([^\]]*)\]\(\s*<?([^)\s>]+)>?(\s+"[^"]*")?\s*\)/g, (_, bang, text, target, title = '') =>
        `${bang}[${text}](${resolve(target, bang === '!')}${title})`)
      // the outer link of a badge, [![alt](image)](target), whose text holds brackets
      .replace(/(\)\]\(\s*)<?([^)\s>]+)>?(\s+"[^"]*")?(\s*\))/g, (_, head, target, title = '', tail) =>
        `${head}${resolve(target, false)}${title}${tail}`)
      // [label]: target (reference definitions)
      .replace(/^(\s{0,3}\[[^\]]+\]:\s*)(\S+)/, (_, head, target) => head + resolve(target, false))
      // <img src="…">, <a href="…">
      .replace(/\b(src|href)="([^"]+)"/g, (_, attr, target) => `${attr}="${resolve(target, attr === 'src')}"`)
      // GitHub alerts
      .replace(/^(\s*>\s*)\[!(NOTE|TIP|IMPORTANT|WARNING|CAUTION)\]\s*$/, (_, q, kind) => `${q}**${kind[0]}${kind.slice(1).toLowerCase()}**`)
  )
}

let fence = null
const out = readFileSync(file, 'utf8')
  .split('\n')
  .map((line) => {
    const m = line.match(/^\s*(`{3,}|~{3,})/)
    if (m) {
      if (!fence) fence = m[1]
      else if (m[1][0] === fence[0] && m[1].length >= fence.length) fence = null
      return line
    }
    return fence ? line : convertLine(line)
  })
  .join('\n')

const banner = `> **Source code, issues and release notes:** [github.com/${repo}](${home})`
let text = out.includes('<!-- dockerhub:github-link -->')
  ? out.replace('<!-- dockerhub:github-link -->', banner)
  : `${banner}\n\n${out}`

if (Buffer.byteLength(text) > LIMIT) {
  const tail = `\n\n---\n\n*The rest of this page is on [GitHub](${home}#readme).*\n`
  let cut = text.slice(0, LIMIT - Buffer.byteLength(tail) - 4)
  while (Buffer.byteLength(cut) > LIMIT - Buffer.byteLength(tail)) cut = cut.slice(0, -100)
  text = cut.slice(0, cut.lastIndexOf('\n\n')) + tail
}

process.stdout.write(text)

function originRepo() {
  const url = execSync('git remote get-url origin', { encoding: 'utf8' }).trim()
  return url.replace(/^.*github\.com[:/]/, '').replace(/\.git$/, '')
}
