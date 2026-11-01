"""Conservative page-level reading order for columns and numeric tables."""

import re
from statistics import median


def _box(block):
    points = block.get("Polygon") or ()
    if not points:
        return None
    xs = [float(point["X"]) for point in points]
    ys = [float(point["Y"]) for point in points]
    return min(xs), min(ys), max(xs), max(ys)


def reorder_two_columns(page):
    blocks = page.get("TextBlocks", ())
    if len(blocks) < 12:
        return list(blocks), False
    width = float(page["Width"])
    height = float(page["Height"])
    if width <= 0 or height <= 0:
        return list(blocks), False
    boxes = [_box(block) for block in blocks]
    if any(box is None for box in boxes):
        return list(blocks), False

    for fraction in (0.50, 0.45, 0.55, 0.40, 0.60, 0.35, 0.65):
        split = width * fraction
        gutter = max(6.0, width * 0.015)
        left, right, crossing = [], [], []
        for index, box in enumerate(boxes):
            if box[2] <= split - gutter:
                left.append(index)
            elif box[0] >= split + gutter:
                right.append(index)
            else:
                crossing.append(index)
        if len(left) < 6 or len(right) < 6:
            continue
        if min(len(left), len(right)) < len(blocks) * 0.20:
            continue
        left_top = min(boxes[index][1] for index in left)
        left_bottom = max(boxes[index][3] for index in left)
        right_top = min(boxes[index][1] for index in right)
        right_bottom = max(boxes[index][3] for index in right)
        overlap = max(0.0, min(left_bottom, right_bottom) - max(left_top, right_top))
        if (overlap < 0.55 * min(left_bottom - left_top, right_bottom - right_top)
                or min(left_bottom - left_top, right_bottom - right_top) < height * 0.25):
            continue
        prefix, suffix = [], []
        for index in crossing:
            center_y = (boxes[index][1] + boxes[index][3]) / 2
            if center_y < min(left_top, right_top) - gutter:
                prefix.append(index)
            elif center_y > max(left_bottom, right_bottom) + gutter:
                suffix.append(index)
            else:
                break
        else:
            def ordered(indices):
                return sorted(indices, key=lambda index: (boxes[index][1], boxes[index][0], index))

            order = ordered(prefix) + ordered(left) + ordered(right) + ordered(suffix)
            return [blocks[index] for index in order], order != list(range(len(blocks)))
    return list(blocks), False


def reorder_numeric_table(page):
    blocks = page.get("TextBlocks", ())
    if len(blocks) < 16:
        return list(blocks), False
    width = float(page["Width"])
    if width <= 0:
        return list(blocks), False
    boxes = [_box(block) for block in blocks]
    if any(box is None for box in boxes):
        return list(blocks), False
    heights = [box[3] - box[1] for box in boxes]
    line_height = median(heights)
    if line_height <= 0:
        return list(blocks), False

    tolerance = width * 0.08
    columns = []
    for index in sorted(range(len(blocks)), key=lambda item: boxes[item][0]):
        x = boxes[index][0]
        if not columns or x - median(boxes[item][0] for item in columns[-1]) > tolerance:
            columns.append([])
        columns[-1].append(index)
    if len(columns) != 3:
        return list(blocks), False
    columns = [sorted(column, key=lambda item: boxes[item][1]) for column in columns]
    prefix = []
    while len(columns[0]) > len(columns[1]) and columns[0]:
        first = columns[0][0]
        if boxes[first][1] >= min(boxes[column[0]][1] for column in columns[1:]) - 1.5 * line_height:
            break
        prefix.append(columns[0].pop(0))
    count = len(columns[0])
    if count < 5 or any(len(column) != count for column in columns):
        return list(blocks), False
    numeric_cells = [blocks[index].get("Text", "") for column in columns[1:] for index in column]
    if sum(bool(re.search(r"\d", text)) for text in numeric_cells) < len(numeric_cells) * 0.6:
        return list(blocks), False
    pitches = [boxes[column[row + 1]][1] - boxes[column[row]][1]
               for column in columns for row in range(count - 1)]
    row_pitch = median(pitches)
    if row_pitch < line_height * 1.5:
        return list(blocks), False
    for row in range(count):
        positions = [boxes[column[row]][1] for column in columns]
        if max(positions) - min(positions) > row_pitch * 0.55:
            return list(blocks), False

    order = prefix + [column[row] for row in range(count) for column in columns]
    return [blocks[index] for index in order], True


def reorder_four_column_numeric_table(page):
    blocks = page.get("TextBlocks", ())
    if len(blocks) < 20:
        return list(blocks), False
    width = float(page.get("Width") or 0)
    if width <= 0:
        return list(blocks), False
    boxes = [_box(block) for block in blocks]
    if any(box is None for box in boxes):
        return list(blocks), False
    tolerance = width * 0.08
    groups = []
    for index in sorted(range(len(blocks)), key=lambda item: boxes[item][0]):
        if not groups or boxes[index][0] - median(boxes[item][0] for item in groups[-1]) > tolerance:
            groups.append([])
        groups[-1].append(index)
    columns = [sorted(group, key=lambda item: boxes[item][1]) for group in groups if len(group) >= 5]
    if len(columns) != 4 or len({len(column) for column in columns}) != 1:
        return list(blocks), False
    count = len(columns[0])
    numeric_cells = [blocks[index].get("Text", "") for column in columns[1:] for index in column]
    if sum(bool(re.search(r"\d", text)) for text in numeric_cells) < len(numeric_cells) * 0.7:
        return list(blocks), False
    line_height = median(box[3] - box[1] for box in boxes)
    pitches = [boxes[column[row + 1]][1] - boxes[column[row]][1]
               for column in columns for row in range(count - 1)]
    row_pitch = median(pitches)
    if row_pitch < line_height * 0.9:
        return list(blocks), False
    offsets = []
    for column in columns:
        differences = [boxes[column[row]][1] - boxes[columns[0][row]][1] for row in range(count)]
        offsets.append((median(differences[:min(3, count)]), median(differences[-min(3, count):])))
    if any(abs((boxes[column[row]][1] - boxes[columns[0][row]][1])
               - (offsets[column_index][0]
                  + (offsets[column_index][1] - offsets[column_index][0]) * row / (count - 1)))
           > row_pitch * 0.45
           for column_index, column in enumerate(columns) for row in range(count)):
        return list(blocks), False
    used = {index for column in columns for index in column}
    extras = [index for index in range(len(blocks)) if index not in used]
    first = min(boxes[column[0]][1] for column in columns)
    last = max(boxes[column[-1]][1] for column in columns)
    if any(first - row_pitch * 0.3 <= boxes[index][1] <= last + row_pitch * 0.3 for index in extras):
        return list(blocks), False
    prefix = sorted((index for index in extras if boxes[index][1] < first), key=lambda item: boxes[item][1])
    suffix = sorted((index for index in extras if boxes[index][1] >= first), key=lambda item: boxes[item][1])
    order = prefix + [column[row] for row in range(count) for column in columns] + suffix
    return [blocks[index] for index in order], True
