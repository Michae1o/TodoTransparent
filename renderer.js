let todos = [];
let isPassthrough = false;

const todoList = document.getElementById('todo-list');
const todoInput = document.getElementById('todo-input');
const btnAdd = document.getElementById('btn-add');
const btnPassthrough = document.getElementById('btn-passthrough');
const btnMinimize = document.getElementById('btn-minimize');
const btnClose = document.getElementById('btn-close');
const app = document.getElementById('app');
const resizeHandle = document.querySelector('.resize-handle');

async function init() {
  todos = await window.electronAPI.loadTodos();
  render();
}

function save() {
  window.electronAPI.saveTodos(todos);
}

function render() {
  todoList.innerHTML = '';
  todos.forEach((todo, index) => {
    const li = document.createElement('li');
    li.className = 'todo-item' + (todo.completed ? ' completed' : '');
    li.draggable = true;
    li.dataset.index = index;

    li.innerHTML = `
      <div class="drag-handle" title="拖拽排序">
        <span></span><span></span><span></span>
      </div>
      <input type="checkbox" ${todo.completed ? 'checked' : ''}>
      <div class="todo-text" contenteditable="true">${escapeHtml(todo.text)}</div>
      <button class="btn-delete" title="删除">✕</button>
    `;

    const checkbox = li.querySelector('input[type="checkbox"]');
    checkbox.addEventListener('change', () => {
      todo.completed = checkbox.checked;
      save();
      render();
    });

    const textDiv = li.querySelector('.todo-text');
    textDiv.addEventListener('blur', () => {
      const newText = textDiv.innerText.trim();
      if (newText === '') {
        todos.splice(index, 1);
        save();
        render();
      } else if (newText !== todo.text) {
        todo.text = newText;
        save();
      }
    });
    textDiv.addEventListener('keydown', (e) => {
      if (e.key === 'Enter') {
        e.preventDefault();
        textDiv.blur();
      }
    });

    const btnDelete = li.querySelector('.btn-delete');
    btnDelete.addEventListener('click', () => {
      todos.splice(index, 1);
      save();
      render();
    });

    // 拖拽排序
    li.addEventListener('dragstart', (e) => {
      li.classList.add('dragging');
      e.dataTransfer.effectAllowed = 'move';
      e.dataTransfer.setData('text/plain', index);
    });
    li.addEventListener('dragend', () => {
      li.classList.remove('dragging');
    });
    li.addEventListener('dragover', (e) => {
      e.preventDefault();
      e.dataTransfer.dropEffect = 'move';
    });
    li.addEventListener('drop', (e) => {
      e.preventDefault();
      const fromIndex = parseInt(e.dataTransfer.getData('text/plain'));
      if (fromIndex === index) return;
      const [moved] = todos.splice(fromIndex, 1);
      todos.splice(index, 0, moved);
      save();
      render();
    });

    todoList.appendChild(li);
  });
}

function escapeHtml(text) {
  const div = document.createElement('div');
  div.textContent = text;
  return div.innerHTML;
}

function addTodo() {
  const text = todoInput.value.trim();
  if (!text) return;
  todos.unshift({ text, completed: false });
  todoInput.value = '';
  save();
  render();
}

btnAdd.addEventListener('click', addTodo);
todoInput.addEventListener('keydown', (e) => {
  if (e.key === 'Enter') addTodo();
});

// 鼠标穿透切换
btnPassthrough.addEventListener('click', () => {
  isPassthrough = !isPassthrough;
  if (isPassthrough) {
    btnPassthrough.classList.add('active');
    app.classList.add('passthrough-mode');
    window.electronAPI.setIgnoreMouseEvents(true, { forward: true });
  } else {
    btnPassthrough.classList.remove('active');
    app.classList.remove('passthrough-mode');
    window.electronAPI.setIgnoreMouseEvents(false);
  }
});

btnMinimize.addEventListener('click', () => {
  window.electronAPI.minimizeWindow();
});

btnClose.addEventListener('click', () => {
  window.electronAPI.closeWindow();
});

// 窗口拖拽（标题栏区域由 app-region 处理，这里做后备）
let isDragging = false;
let dragStartX, dragStartY;

const titlebar = document.querySelector('.titlebar');
titlebar.addEventListener('mousedown', (e) => {
  if (e.target.closest('button')) return;
  isDragging = true;
  dragStartX = e.screenX;
  dragStartY = e.screenY;
});

document.addEventListener('mousemove', (e) => {
  if (!isDragging) return;
  const dx = e.screenX - dragStartX;
  const dy = e.screenY - dragStartY;
  dragStartX = e.screenX;
  dragStartY = e.screenY;
  window.electronAPI.moveWindow(dx, dy);
});

document.addEventListener('mouseup', () => {
  isDragging = false;
});

// 窗口大小调整
let isResizing = false;
let resizeStartX, resizeStartY, resizeStartW, resizeStartH;

resizeHandle.addEventListener('mousedown', (e) => {
  isResizing = true;
  resizeStartX = e.screenX;
  resizeStartY = e.screenY;
  resizeStartW = window.innerWidth;
  resizeStartH = window.innerHeight;
  e.preventDefault();
});

document.addEventListener('mousemove', (e) => {
  if (!isResizing) return;
  const newW = resizeStartW + (e.screenX - resizeStartX);
  const newH = resizeStartH + (e.screenY - resizeStartY);
  window.electronAPI.resizeWindow(newW, newH);
});

document.addEventListener('mouseup', () => {
  isResizing = false;
});

init();
