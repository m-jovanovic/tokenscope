import { readdir, readFile } from 'node:fs/promises'
import { fileURLToPath } from 'node:url'
import path from 'node:path'

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..')
const output = path.join(root, 'web/dist')
async function* files(directory) {
  for (const entry of await readdir(directory, { withFileTypes: true })) {
    const full = path.join(directory, entry.name)
    if (entry.isDirectory()) yield* files(full)
    else yield full
  }
}
let count = 0
for await (const file of files(output)) {
  const relative = path.relative(output, file).replaceAll('\\', '/')
  if (/rehearsal|studioContent|slideDeck|TokenScope-slides|client-review|video-script|recording-guide|atlas\.excalidraw|pack\.json/i.test(relative))
    throw new Error(`Private recording artifact in public build: ${relative}`)
  if (/\.(js|css|json|html|md|txt|map)$/i.test(file)) {
    const content = await readFile(file, 'utf8')
    if (/tokenscope-rehearsal-v1|Understand it before the take|Your recording guide|recording-cues|Copy editable atlas|Diagrams to think with|Draft for client review|CLAUDE CODE \+ CODEX|C:\\\\Users\\\\[^\\]/i.test(content))
      throw new Error(`Private or retired content in public build: ${relative}`)
  }
  count++
}
if (count === 0) throw new Error('Public build is empty.')
console.log(`Public artifact check passed: ${count} files, no recording pack or private studio.`)
