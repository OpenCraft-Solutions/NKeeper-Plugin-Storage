import { adoptStyles, definePlugin } from '@nkeeper/sdk'
import { StorageView } from './StorageView'
import { toolStyles } from './ToolFrame'

const plugin = definePlugin({
  id: 'storage',
  activate() {
    // Constructable stylesheets: the shell's policy blocks <style> elements (docs/architecture.md §7.14).
    adoptStyles(toolStyles)
  },
  sessionViews: { storage: StorageView },
})

export default plugin
