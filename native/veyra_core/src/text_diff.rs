use std::fs;
use std::path::Path;

use serde::Serialize;

use crate::hash::hash_line;

#[derive(Serialize)]
struct TextDiffLine {
    kind: String,
    left_line_number: Option<u32>,
    right_line_number: Option<u32>,
    text: String,
}

#[derive(Serialize)]
struct TextDiffResult {
    added_lines: u32,
    removed_lines: u32,
    is_truncated: bool,
    lines: Vec<TextDiffLine>,
}

#[derive(Clone, Copy)]
enum DiffOp {
    Equal(usize, usize),
    Remove(usize),
    Add(usize),
}

fn load_text_lines_lossy(path: &Path) -> Result<Vec<String>, String> {
    let bytes = fs::read(path).map_err(|e| format!("read {}: {e}", path.display()))?;
    let text = String::from_utf8_lossy(&bytes);
    Ok(text.lines().map(|line| line.to_string()).collect())
}

fn backtrack_myers(
    trace: &[Vec<isize>],
    offset: isize,
    left: &[String],
    right: &[String],
    left_hashes: &[u64],
    right_hashes: &[u64],
) -> Vec<DiffOp> {
    let mut x = left.len() as isize;
    let mut y = right.len() as isize;
    let mut ops = Vec::new();

    for d_index in (1..trace.len()).rev() {
        let d = (d_index - 1) as isize;
        let v_prev = &trace[d_index - 1];
        let k = x - y;

        let prev_k = if k == -d
            || (k != d && v_prev[(k - 1 + offset) as usize] < v_prev[(k + 1 + offset) as usize])
        {
            k + 1
        } else {
            k - 1
        };

        let prev_x = v_prev[(prev_k + offset) as usize];
        let prev_y = prev_x - prev_k;

        while x > prev_x
            && y > prev_y
            && left_hashes[(x - 1) as usize] == right_hashes[(y - 1) as usize]
            && left[(x - 1) as usize] == right[(y - 1) as usize]
        {
            x -= 1;
            y -= 1;
            ops.push(DiffOp::Equal(x as usize, y as usize));
        }

        if d == 0 {
            break;
        }

        if x == prev_x {
            y -= 1;
            ops.push(DiffOp::Add(y as usize));
        } else {
            x -= 1;
            ops.push(DiffOp::Remove(x as usize));
        }
    }

    while x > 0
        && y > 0
        && left_hashes[(x - 1) as usize] == right_hashes[(y - 1) as usize]
        && left[(x - 1) as usize] == right[(y - 1) as usize]
    {
        x -= 1;
        y -= 1;
        ops.push(DiffOp::Equal(x as usize, y as usize));
    }

    while x > 0 {
        x -= 1;
        ops.push(DiffOp::Remove(x as usize));
    }

    while y > 0 {
        y -= 1;
        ops.push(DiffOp::Add(y as usize));
    }

    ops.reverse();
    ops
}

fn myers_diff_ops(left: &[String], right: &[String]) -> Vec<DiffOp> {
    let n = left.len();
    let m = right.len();

    if n == 0 {
        return (0..m).map(DiffOp::Add).collect();
    }

    if m == 0 {
        return (0..n).map(DiffOp::Remove).collect();
    }

    let left_hashes: Vec<u64> = left.iter().map(|line| hash_line(line)).collect();
    let right_hashes: Vec<u64> = right.iter().map(|line| hash_line(line)).collect();

    let max = n + m;
    let offset = max as isize;

    let mut v = vec![0isize; 2 * max + 1];
    let mut trace: Vec<Vec<isize>> = Vec::with_capacity(max + 2);

    for d in 0..=max {
        trace.push(v.clone());

        let mut k = -(d as isize);
        while k <= d as isize {
            let idx = (k + offset) as usize;

            let mut x = if k == -(d as isize)
                || (k != d as isize && v[(k - 1 + offset) as usize] < v[(k + 1 + offset) as usize])
            {
                v[(k + 1 + offset) as usize]
            } else {
                v[(k - 1 + offset) as usize] + 1
            };

            let mut y = x - k;
            while x < n as isize
                && y < m as isize
                && left_hashes[x as usize] == right_hashes[y as usize]
                && left[x as usize] == right[y as usize]
            {
                x += 1;
                y += 1;
            }

            v[idx] = x;

            if x >= n as isize && y >= m as isize {
                trace.push(v.clone());
                return backtrack_myers(&trace, offset, left, right, &left_hashes, &right_hashes);
            }

            k += 2;
        }
    }

    let mut fallback = Vec::with_capacity(n + m);
    for idx in 0..n {
        fallback.push(DiffOp::Remove(idx));
    }
    for idx in 0..m {
        fallback.push(DiffOp::Add(idx));
    }
    fallback
}

pub fn build_text_diff_json(
    left_path: &Path,
    right_path: &Path,
    max_lines: usize,
) -> Result<Vec<u8>, String> {
    if !left_path.exists() {
        return Err(format!("left file not found: {}", left_path.display()));
    }

    if !right_path.exists() {
        return Err(format!("right file not found: {}", right_path.display()));
    }

    if !left_path.is_file() || !right_path.is_file() {
        return Err("diff paths must point to files".to_string());
    }

    let mut left_lines = load_text_lines_lossy(left_path)?;
    let mut right_lines = load_text_lines_lossy(right_path)?;

    let is_truncated = left_lines.len() > max_lines || right_lines.len() > max_lines;
    if is_truncated {
        left_lines.truncate(max_lines);
        right_lines.truncate(max_lines);
    }

    let ops = myers_diff_ops(&left_lines, &right_lines);

    let mut lines = Vec::with_capacity(ops.len());
    let mut added_lines = 0u32;
    let mut removed_lines = 0u32;

    let mut left_line_number = 1u32;
    let mut right_line_number = 1u32;

    for op in ops {
        match op {
            DiffOp::Equal(left_idx, right_idx) => {
                lines.push(TextDiffLine {
                    kind: "equal".to_string(),
                    left_line_number: Some(left_line_number),
                    right_line_number: Some(right_line_number),
                    text: left_lines[left_idx].clone(),
                });

                left_line_number += 1;
                right_line_number += 1;

                let _ = right_idx;
            }
            DiffOp::Remove(left_idx) => {
                lines.push(TextDiffLine {
                    kind: "remove".to_string(),
                    left_line_number: Some(left_line_number),
                    right_line_number: None,
                    text: left_lines[left_idx].clone(),
                });

                left_line_number += 1;
                removed_lines += 1;
            }
            DiffOp::Add(right_idx) => {
                lines.push(TextDiffLine {
                    kind: "add".to_string(),
                    left_line_number: None,
                    right_line_number: Some(right_line_number),
                    text: right_lines[right_idx].clone(),
                });

                right_line_number += 1;
                added_lines += 1;
            }
        }
    }

    let payload = TextDiffResult {
        added_lines,
        removed_lines,
        is_truncated,
        lines,
    };

    serde_json::to_vec(&payload).map_err(|e| format!("serialize text diff result: {e}"))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn myers_diff_detects_add_and_remove() {
        let left = vec!["a".to_string(), "b".to_string(), "c".to_string()];
        let right = vec![
            "a".to_string(),
            "x".to_string(),
            "c".to_string(),
            "d".to_string(),
        ];

        let ops = myers_diff_ops(&left, &right);

        let mut added = 0;
        let mut removed = 0;

        for op in ops {
            match op {
                DiffOp::Add(_) => added += 1,
                DiffOp::Remove(_) => removed += 1,
                DiffOp::Equal(_, _) => {}
            }
        }

        assert_eq!(added, 2);
        assert_eq!(removed, 1);
    }

    #[test]
    fn myers_diff_is_empty_for_equal_inputs() {
        let left = vec!["same".to_string(), "lines".to_string()];
        let right = vec!["same".to_string(), "lines".to_string()];

        let ops = myers_diff_ops(&left, &right);

        assert!(ops.iter().all(|op| matches!(op, DiffOp::Equal(_, _))));
    }
}
